using System;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace FurkanGozukara.SwarmExtensions.MegapixelResolution;

/// <summary>Adds an aspect-aware megapixel resolution control.</summary>
public class MegapixelResolutionExtension : Extension
{
    private static bool _initialized;
    private static T2IRegisteredParam<double> Megapixels;

    public override void PopulateMetadata()
    {
        ExtensionAuthor = "Furkan Gozukara";
        Description = "Adds a megapixel slider and live diagram for aspect-aware image resolutions.";
        License = "MIT";
        Version = "1.1.0";
        ReadmeURL = "https://github.com/FurkanGozukara/SwarmUI_Premium_Extensions/tree/main/MegapixelResolution";
    }

    public override void OnInit()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;

        Megapixels = T2IParamTypes.Register<double>(new(
            "Megapixels",
            "Target image size in millions of pixels. The width and height are selected automatically from the current Aspect Ratio and rounded to a resolution supported by the selected model.",
            "1", Min: 0.1, Max: 64, ViewMin: 0.1, ViewMax: 4, Step: 0.1,
            OrderPriority: -10.5, ViewType: ParamViewType.SLIDER,
            Group: T2IParamTypes.GroupResolution, Toggleable: true));

        T2IParamInput.SpecialParameterHandlers.Add(ApplyMegapixelResolution);
        ScriptFiles.Add("Assets/megapixel_resolution.js");
        StyleSheetFiles.Add("Assets/megapixel_resolution_preview.css");
    }

    private static void ApplyMegapixelResolution(T2IParamInput input)
    {
        if (!input.TryGet(Megapixels, out double megapixels) || !double.IsFinite(megapixels) || megapixels <= 0)
        {
            return;
        }

        string aspect = input.Get(T2IParamTypes.AspectRatio, "Custom");
        (int referenceWidth, int referenceHeight) = T2IParamTypes.AspectRatioToSizeReference(aspect);
        if (referenceWidth <= 0 || referenceHeight <= 0)
        {
            referenceWidth = input.GetImageWidth();
            referenceHeight = input.GetImageHeight();
        }
        if (referenceWidth <= 0 || referenceHeight <= 0)
        {
            referenceWidth = referenceHeight = 512;
        }

        double ratio = referenceWidth / (double)referenceHeight;
        double targetPixels = megapixels * 1_000_000;
        int precision = input.Get(T2IParamTypes.Model)?.ModelClass?.CompatClass?.ResolutionPrecision ?? 16;
        int width = (int)Utilities.RoundToPrecision(Math.Sqrt(targetPixels * ratio), precision);
        int height = (int)Utilities.RoundToPrecision(Math.Sqrt(targetPixels / ratio), precision);
        width = Math.Clamp(width, 64, 16384);
        height = Math.Clamp(height, 64, 16384);

        input.Set(T2IParamTypes.Width, width);
        input.Set(T2IParamTypes.Height, height);
        input.Remove(T2IParamTypes.SideLength);
        input.Remove(T2IParamTypes.AltResolutionHeightMult);
        input.Remove(T2IParamTypes.RawResolution);
    }
}
