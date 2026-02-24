using System;
using System.Collections.Generic;
using System.Linq;
using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace FurkanGozukara.SwarmExtensions.Ltxv2LatentUpscale;

public class Ltxv2LatentUpscaleExtension : Extension
{
    private static bool _patched;

    public override void OnInit()
    {
        ExtensionAuthor = "Furkan Gozukara";
        Description = "Adds LTXV2 latent upscaler support for Image-to-Video workflows only.";
        License = "MIT";
        Version = "0.5.1";

        if (_patched)
        {
            Logs.Info("LTXV2 I2V Latent Upscale extension already patched.");
            return;
        }
        _patched = true;

        PatchWorkflowSteps();
        Logs.Info("LTXV2 I2V Latent Upscale extension initialized - only affects LTXV2 Image-to-Video with latent upscaling.");
    }

    private static void PatchWorkflowSteps()
    {
        _ = WorkflowGenerator.Steps;

        // Find and wrap the ImageToVideo step (priority 11)
        List<WorkflowGenerator.WorkflowGenStep> steps = WorkflowGenerator.Steps;
        int i2vIndex = steps.FindIndex(step => Math.Abs(step.Priority - 11) < 0.0001);
        
        if (i2vIndex >= 0)
        {
            var originalI2VAction = steps[i2vIndex].Action;
            steps[i2vIndex] = new WorkflowGenerator.WorkflowGenStep(g =>
            {
                // Save reference to original image BEFORE I2V encoding modifies it
                JArray originalInputImage = null;
                if (g.CurrentMedia is not null)
                {
                    originalInputImage = new JArray(g.CurrentMedia.AsRawImage(g.CurrentVae).Path);
                }

                // Check if we should use upscale workflow
                bool shouldUpscale = ShouldApplyI2VUpscale(g);

                if (shouldUpscale)
                {
                    Logs.Info("Using upscale workflow, skipping base I2V workflow");
                    // Don't call originalI2VAction - we'll create complete workflow in upscale
                    TryApplyLtxv2I2VUpscale(g, originalInputImage);
                }
                else
                {
                    // Normal I2V workflow without upscaling
                    originalI2VAction(g);
                }
            }, 11);
            Logs.Debug("Wrapped ImageToVideo step (priority 11)");
        }
        else
        {
            Logs.Warning("Could not find ImageToVideo step to patch");
        }

        // Find and wrap the Refiner step (last priority -4)
        int refinerIndex = steps.FindLastIndex(step => Math.Abs(step.Priority - (-4)) < 0.0001);

        if (refinerIndex >= 0)
        {
            var originalRefinerAction = steps[refinerIndex].Action;
            steps[refinerIndex] = new WorkflowGenerator.WorkflowGenStep(g =>
            {
                // Skip refiner ONLY for LTXV2 I2V with latent upscaling
                if (ShouldSkipRefinerForLtxv2I2V(g))
                {
                    Logs.Info("Skipping refiner for LTXV2 I2V with latent upscaling (handled in video workflow)");
                    return;
                }

                // Otherwise run original refiner
                originalRefinerAction(g);
            }, -4);
            Logs.Debug("Wrapped Refiner step (priority -4)");
        }
        else
        {
            Logs.Warning("Could not find Refiner step to patch");
        }

        WorkflowGenerator.Steps = [.. steps.OrderBy(step => step.Priority)];
    }

    private static bool ShouldApplyI2VUpscale(WorkflowGenerator g)
    {
        return TryGetLtxv2I2vUpscaleSettings(g, out _, out _, out _, out _);
    }

    private static bool ShouldSkipRefinerForLtxv2I2V(WorkflowGenerator g)
    {
        return TryGetLtxv2I2vUpscaleSettings(g, out _, out _, out _, out _);
    }

    private static bool TryGetLtxv2I2vUpscaleSettings(WorkflowGenerator g, out T2IModel videoModel, out double refineUpscale, out string upscaleMethod, out double refinerControl)
    {
        videoModel = null;
        refineUpscale = 1;
        upscaleMethod = null;
        refinerControl = 0;

        if (!g.UserInput.TryGet(T2IParamTypes.VideoModel, out videoModel))
            return false;

        if (videoModel.ModelClass?.CompatClass?.ID != T2IModelClassSorter.CompatLtxv2.ID)
            return false;

        // Only apply to Image-to-Video
        if (!g.UserInput.TryGet(T2IParamTypes.InitImage, out _))
            return false;

        if (!g.UserInput.TryGet(T2IParamTypes.RefinerUpscale, out refineUpscale) || refineUpscale == 1)
            return false;

        upscaleMethod = g.UserInput.Get(ComfyUIBackendExtension.RefinerUpscaleMethod, "None");
        if (!upscaleMethod.StartsWith("latentmodel-"))
            return false;

        if (!g.UserInput.TryGet(T2IParamTypes.RefinerControl, out refinerControl) || refinerControl <= 0)
            return false;

        return true;
    }

    private static void TryApplyLtxv2I2VUpscale(WorkflowGenerator g, JArray originalInputImage = null)
    {
        if (!TryGetLtxv2I2vUpscaleSettings(g, out T2IModel videoModel, out double refineUpscale, out string upscaleMethod, out double refinerControl))
        {
            Logs.Warning("LTXV2 I2V latent upscale was requested but conditions were not met.");
            return;
        }

        Logs.Info($"Applying LTXV2 I2V latent upscale: {upscaleMethod}, scale={refineUpscale}x, control={refinerControl}");

        JArray imageToScale = originalInputImage ?? g.CurrentMedia?.AsRawImage(g.CurrentVae)?.Path;
        if (imageToScale is null)
        {
            Logs.Error("No input image found for LTXV2 I2V upscale.");
            return;
        }

        int? frames = g.UserInput.TryGet(T2IParamTypes.VideoFrames, out int framesRaw) ? framesRaw : null;
        int? videoFps = g.UserInput.TryGet(T2IParamTypes.VideoFPS, out int fpsRaw) ? fpsRaw : null;
        double? videoCfg = g.UserInput.GetNullable(T2IParamTypes.CFGScale, T2IParamInput.SectionID_Video, false)
            ?? g.UserInput.GetNullable(T2IParamTypes.VideoCFG, T2IParamInput.SectionID_Video);
        int videoSteps = g.UserInput.GetNullable(T2IParamTypes.Steps, T2IParamInput.SectionID_Video, false)
            ?? g.UserInput.Get(T2IParamTypes.VideoSteps, 20, sectionId: T2IParamInput.SectionID_Video);
        string resFormat = g.UserInput.Get(T2IParamTypes.VideoResolution, "Model Preferred");
        long seed = g.UserInput.Get(T2IParamTypes.Seed) + 42;
        string prompt = g.UserInput.Get(T2IParamTypes.Prompt, "");
        string negPrompt = g.UserInput.Get(T2IParamTypes.NegativePrompt, "");

        int width = videoModel.StandardWidth <= 0 ? 1024 : videoModel.StandardWidth;
        int height = videoModel.StandardHeight <= 0 ? 576 : videoModel.StandardHeight;
        int imageWidth = g.UserInput.GetImageWidth();
        int imageHeight = g.UserInput.GetImageHeight();
        int resPrecision = 64;
        if (videoModel.ModelClass?.CompatClass?.ID == "hunyuan-video")
        {
            resPrecision = 16;
        }
        if (resFormat == "Image Aspect, Model Res")
        {
            if (width == 1024 && height == 576 && imageWidth == 1344 && imageHeight == 768)
            {
                width = 1024;
                height = 576;
            }
            else
            {
                (width, height) = Utilities.ResToModelFit(imageWidth, imageHeight, width * height, resPrecision);
            }
        }
        else if (resFormat == "Image")
        {
            width = imageWidth;
            height = imageHeight;
            width = (int)Math.Round(width * refineUpscale);
            height = (int)Math.Round(height * refineUpscale);
        }

        int targetWidth = width;
        int targetHeight = height;
        int baseWidth = (int)Math.Round(targetWidth / refineUpscale);
        int baseHeight = (int)Math.Round(targetHeight / refineUpscale);
        if (baseWidth <= 0 || baseHeight <= 0)
        {
            Logs.Warning($"Invalid base resolution computed ({baseWidth}x{baseHeight}), falling back to target resolution.");
            baseWidth = Math.Max(16, targetWidth);
            baseHeight = Math.Max(16, targetHeight);
        }

        g.IsImageToVideo = true;
        WorkflowGenerator.ImageToVideoGenInfo genInfo = new()
        {
            Generator = g,
            VideoModel = videoModel,
            VideoSwapModel = g.UserInput.Get(T2IParamTypes.VideoSwapModel, null),
            VideoSwapPercent = g.UserInput.Get(T2IParamTypes.VideoSwapPercent, 0.5),
            Frames = frames,
            VideoCFG = videoCfg,
            VideoFPS = videoFps,
            Width = baseWidth,
            Height = baseHeight,
            Prompt = prompt,
            NegativePrompt = negPrompt,
            Steps = videoSteps,
            Seed = seed,
            ContextID = T2IParamInput.SectionID_Video,
            VideoEndFrame = g.UserInput.Get(T2IParamTypes.VideoEndFrame, null)
        };

        string scaledImage = g.CreateNode("ImageScale", new JObject()
        {
            ["image"] = imageToScale,
            ["width"] = targetWidth,
            ["height"] = targetHeight,
            ["upscale_method"] = "lanczos",
            ["crop"] = "disabled"
        });
        JArray scaledImageOut = [scaledImage, 0];
        g.CurrentMedia = new WGNodeData(scaledImageOut, g, WGNodeData.DT_IMAGE, g.CurrentCompat());

        genInfo.PrepModelAndCond(g);
        genInfo.PrepFullCond(g);
        genInfo.VideoCFG ??= genInfo.DefaultCFG;

        string previewType = g.UserInput.Get(ComfyUIBackendExtension.VideoPreviewType, "animate");
        string explicitSampler = g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, null, sectionId: genInfo.ContextID, includeBase: false);
        string explicitScheduler = g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, null, sectionId: genInfo.ContextID, includeBase: false);
        g.CurrentMedia = g.CurrentMedia.AsSamplingLatent(genInfo.Vae, g.CurrentAudioVae);

        string baseSampler = g.CreateKSampler(genInfo.Model.Path, genInfo.PosCond, genInfo.NegCond, g.CurrentMedia.Path,
            genInfo.VideoCFG.Value, genInfo.Steps, genInfo.StartStep, 10000, genInfo.Seed, false, true,
            sigmin: 0.002, sigmax: 1000, previews: previewType,
            defsampler: genInfo.DefaultSampler, defscheduler: genInfo.DefaultScheduler,
            hadSpecialCond: genInfo.HadSpecialCond, explicitSampler: explicitSampler, explicitScheduler: explicitScheduler,
            sectionId: genInfo.ContextID);

        string separated = g.CreateNode("LTXVSeparateAVLatent", new JObject()
        {
            ["av_latent"] = WorkflowGenerator.NodePath(baseSampler, 0)
        });
        JArray baseVideoLatent = [separated, 0];
        JArray baseAudioLatent = [separated, 1];

        string cropGuides = g.CreateNode("LTXVCropGuides", new JObject()
        {
            ["positive"] = genInfo.PosCond,
            ["negative"] = genInfo.NegCond,
            ["latent"] = baseVideoLatent
        });
        JArray cropPosCond = [cropGuides, 0];
        JArray cropNegCond = [cropGuides, 1];
        JArray cropLatent = [cropGuides, 2];

        string latentModelLoader = g.CreateNode("LatentUpscaleModelLoader", new JObject()
        {
            ["model_name"] = upscaleMethod.After("latentmodel-")
        });
        string latentUpsampler = g.CreateNode("LTXVLatentUpsampler", new JObject()
        {
            ["vae"] = genInfo.Vae.Path,
            ["samples"] = cropLatent,
            ["upscale_model"] = WorkflowGenerator.NodePath(latentModelLoader, 0)
        });

        string preproc = g.CreateNode("LTXVPreprocess", new JObject()
        {
            ["image"] = scaledImageOut,
            ["img_compression"] = 32
        });

        string upscaledImgToVideo = g.CreateNode("LTXVImgToVideoInplace", new JObject()
        {
            ["vae"] = genInfo.Vae.Path,
            ["image"] = WorkflowGenerator.NodePath(preproc, 0),
            ["latent"] = WorkflowGenerator.NodePath(latentUpsampler, 0),
            ["strength"] = 1.0,
            ["bypass"] = false
        });

        string reconcat = g.CreateNode("LTXVConcatAVLatent", new JObject()
        {
            ["video_latent"] = WorkflowGenerator.NodePath(upscaledImgToVideo, 0),
            ["audio_latent"] = baseAudioLatent
        });

        JArray refineModel = genInfo.Model.Path;
        if (g.UserInput.TryGet(ComfyUIBackendExtension.RefinerHyperTile, out int tileSize))
        {
            string hyperTileNode = g.CreateNode("HyperTile", new JObject()
            {
                ["model"] = refineModel,
                ["tile_size"] = tileSize,
                ["swap_size"] = 2,
                ["max_depth"] = 0,
                ["scale_depth"] = false
            });
            refineModel = [hyperTileNode, 0];
        }

        int upscaleSteps = g.UserInput.Get(T2IParamTypes.RefinerSteps, genInfo.Steps, sectionId: T2IParamInput.SectionID_Refiner);
        double upscaleCfg = g.UserInput.Get(T2IParamTypes.RefinerCFGScale, genInfo.VideoCFG.Value, sectionId: T2IParamInput.SectionID_Refiner);
        int upscaleStartStep = (int)Math.Round(upscaleSteps * (1 - refinerControl));
        if (upscaleStartStep < 0)
        {
            upscaleStartStep = 0;
        }
        else if (upscaleStartStep > upscaleSteps)
        {
            upscaleStartStep = upscaleSteps;
        }

        string refinerMethod = g.UserInput.Get(T2IParamTypes.RefinerMethod, "PostApply");
        bool addNoise = refinerMethod != "StepSwapNoisy";
        bool doTiled = g.UserInput.Get(T2IParamTypes.RefinerDoTiling, false);

        string explicitSamplerRef = g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, null, sectionId: T2IParamInput.SectionID_Refiner, includeBase: false)
            ?? g.UserInput.Get(ComfyUIBackendExtension.RefinerSamplerParam, null);
        string explicitSchedulerRef = g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, null, sectionId: T2IParamInput.SectionID_Refiner, includeBase: false)
            ?? g.UserInput.Get(ComfyUIBackendExtension.RefinerSchedulerParam, null);

        string upscaleSampler = g.CreateKSampler(refineModel, cropPosCond, cropNegCond, [reconcat, 0],
            upscaleCfg, upscaleSteps, upscaleStartStep, 10000, genInfo.Seed + 1, false, addNoise,
            sigmin: 0.002, sigmax: 1000, previews: previewType, doTiled: doTiled,
            hadSpecialCond: true, explicitSampler: explicitSamplerRef, explicitScheduler: explicitSchedulerRef,
            sectionId: T2IParamInput.SectionID_Refiner);

        g.CurrentMedia = new WGNodeData([upscaleSampler, 0], g, WGNodeData.DT_LATENT_AUDIOVIDEO, g.CurrentCompat());
        g.CurrentMedia = g.CurrentMedia.AsRawImage(genInfo.Vae);
        int outputFps = genInfo.VideoFPS ?? 24;
        g.CurrentMedia.FPS = outputFps;
        if (g.UserInput.TryGet(T2IParamTypes.TrimVideoStartFrames, out _) || g.UserInput.TryGet(T2IParamTypes.TrimVideoEndFrames, out _))
        {
            string trimNode = g.CreateNode("SwarmTrimFrames", new JObject()
            {
                ["image"] = g.CurrentMedia.Path,
                ["trim_start"] = g.UserInput.Get(T2IParamTypes.TrimVideoStartFrames, 0),
                ["trim_end"] = g.UserInput.Get(T2IParamTypes.TrimVideoEndFrames, 0)
            });
            g.CurrentMedia = g.CurrentMedia.WithPath([trimNode, 0]);
        }

        bool hasExtend = prompt.Contains("<extend:");
        if (!hasExtend && g.UserInput.TryGet(ComfyUIBackendExtension.VideoFrameInterpolationMethod, out string vfiMethod)
            && g.UserInput.TryGet(ComfyUIBackendExtension.VideoFrameInterpolationMultiplier, out int mult) && mult > 1)
        {
            if (g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false))
            {
                g.CurrentMedia.SaveOutput(genInfo.Vae, g.CurrentAudioVae, g.GetStableDynamicID(50000, 0));
            }
            g.CurrentMedia = g.CurrentMedia.WithPath(g.DoInterpolation(g.CurrentMedia.Path, vfiMethod, mult));
            outputFps *= mult;
            g.CurrentMedia.FPS = outputFps;
        }
        string nodeId = hasExtend ? $"{g.GetStableDynamicID(50000, 0)}" : "9";
        g.CurrentMedia.SaveOutput(genInfo.Vae, g.CurrentAudioVae, nodeId);

        RemovePreVideoSaveNode(g);

        g.IsImageToVideo = false;
        Logs.Info("LTXV2 I2V latent upscale completed successfully");
    }

    private static void RemovePreVideoSaveNode(WorkflowGenerator g)
    {
        if (g.Workflow is null || !g.Workflow.TryGetValue("30", out JToken nodeToken))
        {
            return;
        }
        if (nodeToken is not JObject nodeObj)
        {
            return;
        }
        if ($"{nodeObj["class_type"]}" != "SwarmSaveAnimationWS")
        {
            return;
        }
        g.Workflow.Remove("30");
        Logs.Info("Removed pre-video save node 30 for LTXV2 I2V upscale.");
    }
}
