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
        Description = "Adds LTXV2 latent upscaler flow for Refiner Upscale in video pipelines.";
        License = "MIT";
        Version = "0.1.0";

        if (_patched)
        {
            return;
        }
        _patched = true;

        PatchWorkflowSteps();
    }

    private static void PatchWorkflowSteps()
    {
        _ = WorkflowGenerator.Steps;

        ReplaceStep(11, PatchedImageToVideoStep);
        ReplaceStep(12, PatchedExtendVideoStep);
        ReplaceLastStep(-4, PatchedRefinerStep);
    }

    private static void ReplaceStep(double priority, Action<WorkflowGenerator> newAction)
    {
        List<WorkflowGenerator.WorkflowGenStep> steps = WorkflowGenerator.Steps;
        int index = steps.FindIndex(step => Math.Abs(step.Priority - priority) < 0.0001);
        if (index < 0)
        {
            Logs.Warning($"Ltxv2LatentUpscaleExtension could not find workflow step priority {priority} to replace.");
            return;
        }
        steps[index] = new WorkflowGenerator.WorkflowGenStep(newAction, priority);
        WorkflowGenerator.Steps = [.. steps.OrderBy(step => step.Priority)];
    }

    private static void ReplaceLastStep(double priority, Action<WorkflowGenerator> newAction)
    {
        List<WorkflowGenerator.WorkflowGenStep> steps = WorkflowGenerator.Steps;
        int index = steps.FindLastIndex(step => Math.Abs(step.Priority - priority) < 0.0001);
        if (index < 0)
        {
            Logs.Warning($"Ltxv2LatentUpscaleExtension could not find workflow step priority {priority} to replace.");
            return;
        }
        steps[index] = new WorkflowGenerator.WorkflowGenStep(newAction, priority);
        WorkflowGenerator.Steps = [.. steps.OrderBy(step => step.Priority)];
    }

    private static JArray DoMaskShrinkApply(WorkflowGenerator g, JArray imgIn)
    {
        (string boundsNode, string croppedMask, string masked, string scaledImage) = g.MaskShrunkInfo;
        g.MaskShrunkInfo = new(null, null, null, null);
        if (boundsNode is not null)
        {
            imgIn = g.RecompositeCropped(boundsNode, [croppedMask, 0], g.FinalInputImage, imgIn);
        }
        else if (g.UserInput.Get(T2IParamTypes.InitImageRecompositeMask, true) && g.FinalMask is not null && !g.NodeHelpers.ContainsKey("recomposite_mask_result"))
        {
            imgIn = g.CompositeMask(g.FinalInputImage, imgIn, g.FinalMask);
        }
        g.NodeHelpers["recomposite_mask_result"] = $"{imgIn[0]}";
        return imgIn;
    }

    private static void ApplyVideoTrim(WorkflowGenerator g)
    {
        if (g.UserInput.TryGet(T2IParamTypes.TrimVideoStartFrames, out _) || g.UserInput.TryGet(T2IParamTypes.TrimVideoEndFrames, out _))
        {
            string trimNode = g.CreateNode("SwarmTrimFrames", new JObject()
            {
                ["image"] = g.FinalImageOut,
                ["trim_start"] = g.UserInput.Get(T2IParamTypes.TrimVideoStartFrames, 0),
                ["trim_end"] = g.UserInput.Get(T2IParamTypes.TrimVideoEndFrames, 0)
            });
            g.FinalImageOut = [trimNode, 0];
        }
    }

    private static bool TryApplyLtxv2VideoUpscale(WorkflowGenerator g, WorkflowGenerator.ImageToVideoGenInfo genInfo, string explicitSampler, string explicitScheduler)
    {
        string upscaleMethod = g.UserInput.Get(ComfyUIBackendExtension.RefinerUpscaleMethod, "None");
        double refinerControl = g.UserInput.Get(T2IParamTypes.RefinerControl, 0.5);
        if (!g.IsLTXV2())
        {
            return false;
        }
        if (!g.UserInput.TryGet(T2IParamTypes.RefinerUpscale, out double refineUpscale) || refineUpscale == 1)
        {
            return false;
        }
        if (!upscaleMethod.StartsWith("latentmodel-") || refinerControl <= 0)
        {
            return false;
        }

        int upscaleSteps = g.UserInput.Get(T2IParamTypes.RefinerSteps, genInfo.Steps);
        double upscaleCfg = g.UserInput.Get(T2IParamTypes.RefinerCFGScale, genInfo.VideoCFG ?? 3);
        int startStep = (int)Math.Round(upscaleSteps * (1 - refinerControl));
        string upscaleModelLoader = g.CreateNode("LatentUpscaleModelLoader", new JObject()
        {
            ["model_name"] = upscaleMethod.After("latentmodel-")
        });
        string separated = g.CreateNode("LTXVSeparateAVLatent", new JObject()
        {
            ["av_latent"] = g.FinalLatentImage
        });
        JArray videoLatent = [separated, 0];
        JArray audioLatent = [separated, 1];
        g.FinalLatentAudio = audioLatent;
        string cropGuides = g.CreateNode("LTXVCropGuides", new JObject()
        {
            ["positive"] = genInfo.PosCond,
            ["negative"] = genInfo.NegCond,
            ["latent"] = videoLatent
        });
        JArray croppedPos = [cropGuides, 0];
        JArray croppedNeg = [cropGuides, 1];
        JArray croppedLatent = [cropGuides, 2];
        string upscaled = g.CreateNode("LTXVLatentUpsampler", new JObject()
        {
            ["vae"] = genInfo.Vae,
            ["samples"] = croppedLatent,
            ["upscale_model"] = WorkflowGenerator.NodePath(upscaleModelLoader, 0)
        });
        JArray upscaledLatent = [upscaled, 0];
        string ltxvCond = g.CreateNode("LTXVConditioning", new JObject()
        {
            ["positive"] = croppedPos,
            ["negative"] = croppedNeg,
            ["frame_rate"] = genInfo.VideoFPS ?? 24
        });
        JArray upscalePosCond = [ltxvCond, 0];
        JArray upscaleNegCond = [ltxvCond, 1];
        string reconcat = g.CreateNode("LTXVConcatAVLatent", new JObject()
        {
            ["video_latent"] = upscaledLatent,
            ["audio_latent"] = audioLatent
        });
        JArray recombinedLatent = [reconcat, 0];
        string upscaleExplicitSampler = g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, null, sectionId: T2IParamInput.SectionID_Refiner, includeBase: false)
            ?? g.UserInput.Get(ComfyUIBackendExtension.RefinerSamplerParam, null)
            ?? explicitSampler;
        string upscaleExplicitScheduler = g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, null, sectionId: T2IParamInput.SectionID_Refiner, includeBase: false)
            ?? g.UserInput.Get(ComfyUIBackendExtension.RefinerSchedulerParam, null)
            ?? explicitScheduler;
        string previewType = g.UserInput.Get(ComfyUIBackendExtension.VideoPreviewType, "animate");
        string upscaleSampler = g.CreateKSampler(genInfo.Model, upscalePosCond, upscaleNegCond, recombinedLatent, upscaleCfg, upscaleSteps, startStep, 10000,
            genInfo.Seed + 2, false, true, sigmin: 0.002, sigmax: 1000, previews: previewType, defsampler: genInfo.DefaultSampler, defscheduler: genInfo.DefaultScheduler,
            hadSpecialCond: true, explicitSampler: upscaleExplicitSampler, explicitScheduler: upscaleExplicitScheduler, sectionId: T2IParamInput.SectionID_Refiner);
        g.FinalLatentImage = [upscaleSampler, 0];

        string decoded = g.CreateVAEDecode(genInfo.Vae, g.FinalLatentImage);
        g.FinalImageOut = [decoded, 0];
        ApplyVideoTrim(g);
        return true;
    }

    private static void PatchedRefinerStep(WorkflowGenerator g)
    {
        if (g.UserInput.TryGet(T2IParamTypes.RefinerMethod, out string method)
            && g.UserInput.TryGet(T2IParamTypes.RefinerControl, out double refinerControl))
        {
            if (g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel videoModel)
                && videoModel.ModelClass?.CompatClass?.ID == T2IModelClassSorter.CompatLtxv2.ID
                && g.UserInput.TryGet(T2IParamTypes.RefinerUpscale, out _))
            {
                return;
            }
            g.IsRefinerStage = true;
            JArray origVae = g.FinalVae, prompt = g.FinalPrompt, negPrompt = g.FinalNegativePrompt;
            bool modelMustReencode = false;
            T2IModel baseModel = g.UserInput.Get(T2IParamTypes.Model);
            T2IModel refineModel = baseModel;
            string loaderNodeId = null;
            if (g.UserInput.TryGet(T2IParamTypes.RefinerModel, out T2IModel altRefineModel) && altRefineModel is not null)
            {
                refineModel = altRefineModel;
                modelMustReencode = true;
                if (refineModel.ModelClass?.CompatClass == baseModel.ModelClass?.CompatClass)
                {
                    modelMustReencode = false;
                }
                if (refineModel.ModelClass?.CompatClass?.ID == "stable-diffusion-xl-v1-refiner" && baseModel.ModelClass?.CompatClass?.ID == "stable-diffusion-xl-v1")
                {
                    modelMustReencode = false;
                }
                loaderNodeId = "20";
            }
            if (g.UserInput.TryGet(T2IParamTypes.RefinerVAE, out _))
            {
                modelMustReencode = true;
            }
            g.NoVAEOverride = refineModel.ModelClass?.CompatClass != baseModel.ModelClass?.CompatClass;
            g.FinalLoadedModel = refineModel;
            g.FinalLoadedModelList = [refineModel];
            (g.FinalLoadedModel, g.FinalModel, g.FinalClip, g.FinalVae) = g.CreateStandardModelLoader(refineModel, "Refiner", loaderNodeId, sectionId: T2IParamInput.SectionID_Refiner);
            g.NoVAEOverride = false;
            prompt = g.CreateConditioning(g.UserInput.Get(T2IParamTypes.Prompt), g.FinalClip, g.FinalLoadedModel, true, isRefiner: true);
            negPrompt = g.CreateConditioning(g.UserInput.Get(T2IParamTypes.NegativePrompt), g.FinalClip, g.FinalLoadedModel, false, isRefiner: true);
            bool doSave = g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false);
            bool doUspcale = g.UserInput.TryGet(T2IParamTypes.RefinerUpscale, out double refineUpscale) && refineUpscale != 1;
            string upscaleMethod = g.UserInput.Get(ComfyUIBackendExtension.RefinerUpscaleMethod, "None");
            bool doPixelUpscale = doUspcale && (upscaleMethod.StartsWith("pixel-") || upscaleMethod.StartsWith("model-"));
            int width = (int)Math.Round(g.UserInput.GetImageWidth() * refineUpscale);
            int height = (int)Math.Round(g.UserInput.GetImageHeight() * refineUpscale);
            width = (width / 16) * 16;
            height = (height / 16) * 16;
            if (modelMustReencode || doPixelUpscale || doSave || g.MaskShrunkInfo.BoundsNode is not null)
            {
                g.CreateVAEDecode(origVae, g.FinalSamples, "24");
                JArray pixelsNode = ["24", 0];
                pixelsNode = DoMaskShrinkApply(g, pixelsNode);
                if (doSave)
                {
                    g.CreateImageSaveNode(pixelsNode, "29");
                }
                if (doPixelUpscale)
                {
                    if (upscaleMethod.StartsWith("pixel-"))
                    {
                        g.CreateNode("ImageScale", new JObject()
                        {
                            ["image"] = pixelsNode,
                            ["width"] = width,
                            ["height"] = height,
                            ["upscale_method"] = upscaleMethod.After("pixel-"),
                            ["crop"] = "disabled"
                        }, "26");
                    }
                    else
                    {
                        g.CreateNode("UpscaleModelLoader", new JObject()
                        {
                            ["model_name"] = upscaleMethod.After("model-")
                        }, "27");
                        g.CreateNode("ImageUpscaleWithModel", new JObject()
                        {
                            ["upscale_model"] = WorkflowGenerator.NodePath("27", 0),
                            ["image"] = pixelsNode
                        }, "28");
                        g.CreateNode("ImageScale", new JObject()
                        {
                            ["image"] = WorkflowGenerator.NodePath("28", 0),
                            ["width"] = width,
                            ["height"] = height,
                            ["upscale_method"] = "lanczos",
                            ["crop"] = "disabled"
                        }, "26");
                    }
                    pixelsNode = ["26", 0];
                    if (refinerControl <= 0)
                    {
                        g.FinalImageOut = pixelsNode;
                        return;
                    }
                }
                if (modelMustReencode || doPixelUpscale)
                {
                    g.CreateVAEEncode(g.FinalVae, pixelsNode, "25");
                    g.FinalSamples = ["25", 0];
                }
            }
            if (doUspcale && upscaleMethod.StartsWith("latent-"))
            {
                g.CreateNode("LatentUpscaleBy", new JObject()
                {
                    ["samples"] = g.FinalSamples,
                    ["upscale_method"] = upscaleMethod.After("latent-"),
                    ["scale_by"] = refineUpscale
                }, "26");
                g.FinalSamples = ["26", 0];
            }
            else if (doUspcale && upscaleMethod.StartsWith("latentmodel-"))
            {
                g.CreateNode("LatentUpscaleModelLoader", new JObject()
                {
                    ["model_name"] = upscaleMethod.After("latentmodel-")
                }, "27");
                if (g.IsHunyuanVideo15())
                {
                    g.CreateNode("HunyuanVideo15LatentUpscaleWithModel", new JObject()
                    {
                        ["model"] = WorkflowGenerator.NodePath("27", 0),
                        ["samples"] = g.FinalSamples,
                        ["upscale_method"] = "bilinear",
                        ["width"] = width,
                        ["height"] = height,
                        ["crop"] = "disabled"
                    }, "26");
                    g.FinalSamples = ["26", 0];
                }
                else if (g.IsLTXV2())
                {
                    string separated = g.CreateNode("LTXVSeparateAVLatent", new JObject()
                    {
                        ["av_latent"] = g.FinalSamples
                    });
                    g.FinalLatentAudio = [separated, 1];
                    string cropGuides = g.CreateNode("LTXVCropGuides", new JObject()
                    {
                        ["positive"] = prompt,
                        ["negative"] = negPrompt,
                        ["latent"] = WorkflowGenerator.NodePath(separated, 0)
                    });
                    prompt = [cropGuides, 0];
                    negPrompt = [cropGuides, 1];
                    g.CreateNode("LTXVLatentUpsampler", new JObject()
                    {
                        ["vae"] = g.FinalVae,
                        ["samples"] = WorkflowGenerator.NodePath(cropGuides, 2),
                        ["upscale_model"] = WorkflowGenerator.NodePath("27", 0)
                    }, "26");
                    string ltxvCond = g.CreateNode("LTXVConditioning", new JObject()
                    {
                        ["positive"] = prompt,
                        ["negative"] = negPrompt,
                        ["frame_rate"] = g.UserInput.Get(T2IParamTypes.Text2VideoFPS, 24)
                    });
                    prompt = [ltxvCond, 0];
                    negPrompt = [ltxvCond, 1];
                    string reconcat = g.CreateNode("LTXVConcatAVLatent", new JObject()
                    {
                        ["video_latent"] = WorkflowGenerator.NodePath("26", 0),
                        ["audio_latent"] = g.FinalLatentAudio
                    });
                    g.FinalSamples = [reconcat, 0];
                }
                else
                {
                    throw new SwarmUserErrorException($"Cannot latent-upscale for {g.CurrentCompatClass()}");
                }
            }
            JArray model = g.FinalModel;
            if (g.UserInput.TryGet(ComfyUIBackendExtension.RefinerHyperTile, out int tileSize))
            {
                string hyperTileNode = g.CreateNode("HyperTile", new JObject()
                {
                    ["model"] = model,
                    ["tile_size"] = tileSize,
                    ["swap_size"] = 2,
                    ["max_depth"] = 0,
                    ["scale_depth"] = false
                });
                model = [hyperTileNode, 0];
            }
            int steps = g.UserInput.Get(T2IParamTypes.RefinerSteps, g.UserInput.Get(T2IParamTypes.Steps, 20, sectionId: T2IParamInput.SectionID_Refiner), sectionId: T2IParamInput.SectionID_Refiner);
            double cfg = g.UserInput.Get(T2IParamTypes.RefinerCFGScale, g.UserInput.Get(T2IParamTypes.CFGScale, 7, sectionId: T2IParamInput.SectionID_Refiner), sectionId: T2IParamInput.SectionID_Refiner);
            string explicitSampler = g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, null, sectionId: T2IParamInput.SectionID_Refiner, includeBase: false) ?? g.UserInput.Get(ComfyUIBackendExtension.RefinerSamplerParam, null);
            string explicitScheduler = g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, null, sectionId: T2IParamInput.SectionID_Refiner, includeBase: false) ?? g.UserInput.Get(ComfyUIBackendExtension.RefinerSchedulerParam, null);
            g.CreateKSampler(model, prompt, negPrompt, g.FinalSamples, cfg, steps, (int)Math.Round(steps * (1 - refinerControl)), 10000,
                g.UserInput.Get(T2IParamTypes.Seed) + 1, false, method != "StepSwapNoisy", id: "23", doTiled: g.UserInput.Get(T2IParamTypes.RefinerDoTiling, false),
                explicitSampler: explicitSampler, explicitScheduler: explicitScheduler, sectionId: T2IParamInput.SectionID_Refiner);
            g.FinalSamples = ["23", 0];
            g.IsRefinerStage = false;
        }
    }

    private static void PatchedImageToVideoStep(WorkflowGenerator g)
    {
        if (g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel vidModel))
        {
            int? frames = g.UserInput.TryGet(T2IParamTypes.VideoFrames, out int framesRaw) ? framesRaw : null;
            int? videoFps = g.UserInput.TryGet(T2IParamTypes.VideoFPS, out int fpsRaw) ? fpsRaw : null;
            double? videoCfg = g.UserInput.GetNullable(T2IParamTypes.CFGScale, T2IParamInput.SectionID_Video, false) ?? g.UserInput.GetNullable(T2IParamTypes.VideoCFG, T2IParamInput.SectionID_Video);
            int steps = g.UserInput.GetNullable(T2IParamTypes.Steps, T2IParamInput.SectionID_Video, false) ?? g.UserInput.Get(T2IParamTypes.VideoSteps, 20, sectionId: T2IParamInput.SectionID_Video);
            string format = g.UserInput.Get(T2IParamTypes.VideoFormat, "h264-mp4").ToLowerFast();
            string resFormat = g.UserInput.Get(T2IParamTypes.VideoResolution, "Model Preferred");
            long seed = g.UserInput.Get(T2IParamTypes.Seed) + 42;
            string prompt = g.UserInput.Get(T2IParamTypes.Prompt, "");
            string negPrompt = g.UserInput.Get(T2IParamTypes.NegativePrompt, "");
            int batchInd = -1, batchLen = -1;
            if (g.UserInput.TryGet(T2IParamTypes.Video2VideoCreativity, out _))
            {
                batchInd = 0;
                batchLen = 1;
            }
            int width = vidModel.StandardWidth <= 0 ? 1024 : vidModel.StandardWidth;
            int height = vidModel.StandardHeight <= 0 ? 576 : vidModel.StandardHeight;
            int imageWidth = g.UserInput.GetImageWidth();
            int imageHeight = g.UserInput.GetImageHeight();
            int resPrecision = 64;
            bool hasLatentUpscaler = false;
            if (vidModel.ModelClass?.CompatClass?.ID == "hunyuan-video")
            {
                resPrecision = 16;
            }
            else if (vidModel.ModelClass?.CompatClass?.ID == T2IModelClassSorter.CompatLtxv2.ID)
            {
                hasLatentUpscaler = true;
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
                if (g.UserInput.TryGet(T2IParamTypes.RefinerUpscale, out double scale) && !hasLatentUpscaler)
                {
                    width = (int)Math.Round(width * scale);
                    height = (int)Math.Round(height * scale);
                }
            }
            void altLatent(WorkflowGenerator.ImageToVideoGenInfo genInfo)
            {
                if (g.UserInput.TryGet(T2IParamTypes.Video2VideoCreativity, out double v2vCreativity))
                {
                    string fromBatch = g.CreateNode("ImageFromBatch", new JObject()
                    {
                        ["image"] = g.FinalImageOut,
                        ["batch_index"] = 0,
                        ["length"] = genInfo.Frames.Value
                    });
                    genInfo.StartStep = (int)Math.Floor(steps * (1 - v2vCreativity));
                    string reEncode = g.CreateNode("VAEEncode", new JObject()
                    {
                        ["vae"] = genInfo.Vae,
                        ["pixels"] = WorkflowGenerator.NodePath(fromBatch, 0)
                    });
                    genInfo.Latent = [reEncode, 0];
                }
            }
            WorkflowGenerator.ImageToVideoGenInfo genInfo = new()
            {
                Generator = g,
                VideoModel = vidModel,
                VideoSwapModel = g.UserInput.Get(T2IParamTypes.VideoSwapModel, null),
                VideoSwapPercent = g.UserInput.Get(T2IParamTypes.VideoSwapPercent, 0.5),
                Frames = frames,
                VideoCFG = videoCfg,
                VideoFPS = videoFps,
                Width = width,
                Height = height,
                Prompt = prompt,
                NegativePrompt = negPrompt,
                Steps = steps,
                Seed = seed,
                AltLatent = altLatent,
                BatchIndex = batchInd,
                BatchLen = batchLen,
                ContextID = T2IParamInput.SectionID_Video,
                VideoEndFrame = g.UserInput.Get(T2IParamTypes.VideoEndFrame, null)
            };

            string explicitSampler = g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, null, sectionId: genInfo.ContextID, includeBase: false);
            string explicitScheduler = g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, null, sectionId: genInfo.ContextID, includeBase: false);
            if (genInfo.VideoSwapModel is not null)
            {
                explicitSampler = g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, null, sectionId: T2IParamInput.SectionID_VideoSwap, includeBase: false) ?? explicitSampler;
                explicitScheduler = g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, null, sectionId: T2IParamInput.SectionID_VideoSwap, includeBase: false) ?? explicitScheduler;
            }

            g.CreateImageToVideo(genInfo);
            videoFps = genInfo.VideoFPS;
            TryApplyLtxv2VideoUpscale(g, genInfo, explicitSampler, explicitScheduler);
            bool hasExtend = prompt.Contains("<extend:");
            if (!hasExtend && g.UserInput.TryGet(ComfyUIBackendExtension.VideoFrameInterpolationMethod, out string method) && g.UserInput.TryGet(ComfyUIBackendExtension.VideoFrameInterpolationMultiplier, out int mult) && mult > 1)
            {
                if (g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false))
                {
                    g.CreateAnimationSaveNode(g.FinalImageOut, videoFps.Value, format, g.GetStableDynamicID(50000, 0));
                }
                g.FinalImageOut = g.DoInterpolation(g.FinalImageOut, method, mult);
                videoFps *= mult;
            }
            if (g.UserInput.Get(T2IParamTypes.VideoBoomerang, false))
            {
                string bounced = g.CreateNode("SwarmVideoBoomerang", new JObject()
                {
                    ["images"] = g.FinalImageOut
                });
                g.FinalImageOut = [bounced, 0];
            }
            string nodeId = "9";
            if (hasExtend)
            {
                nodeId = $"{g.GetStableDynamicID(50000, 0)}";
            }
            g.CreateAnimationSaveNode(g.FinalImageOut, videoFps.Value, format, nodeId);
        }
    }

    private static void PatchedExtendVideoStep(WorkflowGenerator g)
    {
        string fullRawPrompt = g.UserInput.Get(T2IParamTypes.Prompt, "");
        if (fullRawPrompt.Contains("<extend:"))
        {
            string negPrompt = g.UserInput.Get(T2IParamTypes.NegativePrompt, "");
            long seed = g.UserInput.Get(T2IParamTypes.Seed) + 600;
            int? videoFps = g.UserInput.TryGet(T2IParamTypes.VideoFPS, out int fpsRaw) ? fpsRaw : null;
            string format = g.UserInput.Get(T2IParamTypes.VideoExtendFormat, "mp4").ToLowerFast();
            int frameExtendOverlap = g.UserInput.Get(T2IParamTypes.VideoExtendFrameOverlap, 9);
            bool saveIntermediate = g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false);
            T2IModel extendModel = g.UserInput.Get(T2IParamTypes.VideoExtendModel, null) ?? throw new SwarmUserErrorException("You have an '<extend:' block in your prompt, but you don't have a 'Video Extend Model' selected.");
            PromptRegion regionalizer = new(fullRawPrompt);
            List<JArray> vidChunks = [g.FinalImageOut];
            JArray conjoinedLast = g.FinalImageOut;
            string getWidthNode = g.CreateNode("SwarmImageWidth", new JObject()
            {
                ["image"] = g.FinalImageOut
            });
            JArray width = [getWidthNode, 0];
            string getHeightNode = g.CreateNode("SwarmImageHeight", new JObject()
            {
                ["image"] = g.FinalImageOut
            });
            JArray height = [getHeightNode, 0];
            PromptRegion.Part[] parts = [.. regionalizer.Parts.Where(p => p.Type == PromptRegion.PartType.Extend)];
            for (int i = 0; i < parts.Length; i++)
            {
                PromptRegion.Part part = parts[i];
                double cfg = g.UserInput.GetNullable(T2IParamTypes.CFGScale, part.ContextID, false) ?? g.UserInput.GetNullable(T2IParamTypes.VideoCFG, part.ContextID) ?? g.UserInput.Get(T2IParamTypes.CFGScale, 7);
                int steps = g.UserInput.GetNullable(T2IParamTypes.Steps, part.ContextID, false) ?? g.UserInput.GetNullable(T2IParamTypes.VideoSteps, part.ContextID) ?? g.UserInput.Get(T2IParamTypes.Steps, 20);
                seed++;
                int? frames = int.Parse(part.DataText);
                string prompt = part.Prompt;
                string frameCountNode = g.CreateNode("SwarmCountFrames", new JObject()
                {
                    ["image"] = g.FinalImageOut
                });
                JArray frameCount = [frameCountNode, 0];
                string fromEndCountNode = g.CreateNode("SwarmIntAdd", new JObject()
                {
                    ["a"] = frameCount,
                    ["b"] = -frameExtendOverlap
                });
                JArray fromEndCount = [fromEndCountNode, 0];
                string partialBatchNode = g.CreateNode("ImageFromBatch", new JObject()
                {
                    ["image"] = g.FinalImageOut,
                    ["batch_index"] = fromEndCount,
                    ["length"] = frameExtendOverlap
                });
                JArray partialBatch = [partialBatchNode, 0];
                g.FinalImageOut = partialBatch;
                WorkflowGenerator.ImageToVideoGenInfo genInfo = new()
                {
                    Generator = g,
                    VideoModel = extendModel,
                    VideoSwapModel = g.UserInput.Get(T2IParamTypes.VideoExtendSwapModel, null),
                    VideoSwapPercent = g.UserInput.Get(T2IParamTypes.VideoExtendSwapPercent, 0.5),
                    Frames = frames,
                    VideoCFG = cfg,
                    VideoFPS = videoFps,
                    Width = width,
                    Height = height,
                    Prompt = prompt,
                    NegativePrompt = negPrompt,
                    Steps = steps,
                    Seed = seed,
                    BatchIndex = 0,
                    BatchLen = frameExtendOverlap,
                    ContextID = part.ContextID
                };

                string explicitSampler = g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, null, sectionId: genInfo.ContextID, includeBase: false);
                string explicitScheduler = g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, null, sectionId: genInfo.ContextID, includeBase: false);
                if (genInfo.VideoSwapModel is not null)
                {
                    explicitSampler = g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, null, sectionId: T2IParamInput.SectionID_VideoSwap, includeBase: false) ?? explicitSampler;
                    explicitScheduler = g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, null, sectionId: T2IParamInput.SectionID_VideoSwap, includeBase: false) ?? explicitScheduler;
                }

                g.CreateImageToVideo(genInfo);
                videoFps = genInfo.VideoFPS;
                TryApplyLtxv2VideoUpscale(g, genInfo, explicitSampler, explicitScheduler);
                if (saveIntermediate)
                {
                    g.CreateAnimationSaveNode(g.FinalImageOut, videoFps.Value, format, g.GetStableDynamicID(50000, 0));
                }
                string cutNode = g.CreateNode("ImageFromBatch", new JObject()
                {
                    ["image"] = g.FinalImageOut,
                    ["batch_index"] = frameExtendOverlap,
                    ["length"] = frames.Value - frameExtendOverlap
                });
                JArray cut = [cutNode, 0];
                g.FinalImageOut = cut;
                vidChunks.Add(g.FinalImageOut);
                string batchedNode = g.CreateNode("ImageBatch", new JObject()
                {
                    ["image1"] = conjoinedLast,
                    ["image2"] = g.FinalImageOut
                });
                conjoinedLast = [batchedNode, 0];
            }
            g.FinalImageOut = conjoinedLast;
            if (g.UserInput.TryGet(ComfyUIBackendExtension.VideoFrameInterpolationMethod, out string method) && g.UserInput.TryGet(ComfyUIBackendExtension.VideoFrameInterpolationMultiplier, out int mult) && mult > 1)
            {
                if (saveIntermediate)
                {
                    g.CreateAnimationSaveNode(g.FinalImageOut, videoFps.Value, format, g.GetStableDynamicID(50000, 0));
                }
                g.FinalImageOut = g.DoInterpolation(g.FinalImageOut, method, mult);
                videoFps *= mult;
            }
            g.CreateAnimationSaveNode(g.FinalImageOut, videoFps.Value, format, "9");
        }
    }
}
