using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace FurkanGozukara.SwarmExtensions.FluxThreeKlein;

/// <summary>Preserve native PNG artwork on this extension's presets without JPEG re-encoding.</summary>
public static class FluxThreeKleinPresetAPI
{
    public static Task<JObject> FluxKleinSavePngPreset(Session session, JObject raw)
    {
        Task<JObject> Fail(string message) => Task.FromResult(new JObject { ["preset_fail"] = message });
        string title = Utilities.StrictFilenameClean(raw.Value<string>("title") ?? "");
        string preview = raw.Value<string>("preview_image") ?? "";
        const string prefix = "data:image/png;base64,";
        if (string.IsNullOrWhiteSpace(title) || raw["param_map"] is not JObject parameters
            || !string.Equals(parameters.Value<string>("fluxkleinadvanced"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("This importer requires a FLUX.3 advanced preset with a valid title and parameters.");
        }
        if (!preview.StartsWith(prefix, StringComparison.Ordinal) || preview.Length > 16 * 1024 * 1024)
        {
            return Fail("Provide an embedded PNG preview smaller than 16 MB.");
        }
        try
        {
            byte[] data = Convert.FromBase64String(preview[prefix.Length..]);
            byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (data.Length < 33 || !data.Take(8).SequenceEqual(signature))
            {
                return Fail("The preview is not a PNG image.");
            }
        }
        catch (FormatException)
        {
            return Fail("The PNG preview contains invalid base64 data.");
        }
        bool edit = raw.Value<bool?>("is_edit") ?? false;
        string editing = raw.Value<string>("editing") ?? title;
        T2IPreset existing = session.User.GetPreset(edit ? editing : title);
        if (existing is not null && !edit)
        {
            return Fail("A preset with that title already exists.");
        }
        T2IPreset preset = new()
        {
            Author = session.User.UserID,
            Title = title,
            Description = raw.Value<string>("description") ?? "",
            ParamMap = parameters.Properties().ToDictionary(p => p.Name, p => p.Value.ToString()),
            PreviewImage = preview,
            IsStarred = raw.Value<bool?>("is_starred") ?? false
        };
        if (edit && existing is not null && editing != title)
        {
            session.User.DeletePreset(editing);
        }
        session.User.SavePreset(preset);
        Interlocked.Increment(ref ModelsAPI.ModelEditID);
        return Task.FromResult(new JObject { ["success"] = true });
    }
}
