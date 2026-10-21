using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace LidAwake;

// The apps routes (docs/API.md 5.6): /app-icon.png, /open-app, /focus-app, /quit-app, /apps-order.
// They run after the checks of section 2.2, so every one needs a session, and a POST the
// X-LidAwake header. The phone only ever sends an id: PhoneApps looks it up and refuses any other
// string, so a path sent by the phone is never opened, fronted, quit or read.
public sealed partial class WebServer
{
    // The apps the phone may open and switch to. Set before Start (none: the routes answer 404 and
    // "apps" is not in features).
    public PhoneApps? Apps { get; set; }

    static string StringField(Dictionary<string, JsonElement> body, string name) =>
        body.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    string DeviceOf(HttpContext c) => Auth.DeviceName(c.Request.Headers.UserAgent.ToString());

    async Task AppIcon(HttpContext c)
    {
        if (Apps is not { } apps) { await SendText(c, 404, "Not found"); return; }
        var id = c.Request.Query["id"].ToString();
        int size = int.TryParse(c.Request.Query["size"].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var asked) ? asked : 256;
        // Else a program of the memory window's last answers (MemoryInfo.IconPng: ids only).
        var png = apps.IconPng(id, size) ?? Memory?.IconPng(id, size);
        if (png == null) { await SendText(c, 404, "No such app."); return; }
        c.Response.ContentType = "image/png";
        c.Response.ContentLength = png.Length;
        await c.Response.Body.WriteAsync(png);
    }

    async Task OpenApp(HttpContext c)
    {
        if (Apps is not { } apps) { await SendText(c, 404, "Not found"); return; }
        var body = await ReadJson(c.Request);
        if (!apps.Open(StringField(body, "id"), DeviceOf(c))) { await SendText(c, 404, "That app isn't on your list any more."); return; }
        Empty(c);
    }

    async Task FocusApp(HttpContext c)
    {
        if (Apps is not { } apps) { await SendText(c, 404, "Not found"); return; }
        var body = await ReadJson(c.Request);
        // A missing or non-text action counts as "focus", like the Mac.
        var action = body.TryGetValue("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "focus" : "focus";
        if (action is not ("focus" or "hide" or "quit")) { await SendText(c, 400, "Unknown action."); return; }
        if (!apps.Focus(StringField(body, "id"), action, DeviceOf(c))) { await SendText(c, 404, "That app isn't open any more."); return; }
        Empty(c);
    }

    async Task QuitApp(HttpContext c)
    {
        if (Apps is not { } apps) { await SendText(c, 404, "Not found"); return; }
        var body = await ReadJson(c.Request);
        switch (apps.Quit(StringField(body, "id"), DeviceOf(c)))
        {
            case null: await SendText(c, 404, "That app isn't on your list any more."); return;
            case false: await SendText(c, 409, "That app isn't open."); return;
            default: Empty(c); return;
        }
    }

    async Task AppsOrder(HttpContext c)
    {
        if (Apps is not { } apps) { await SendText(c, 404, "Not found"); return; }
        var body = await ReadJson(c.Request);
        // `ids` must be a list of strings; anything else is "missing".
        if (!body.TryGetValue("ids", out var list) || list.ValueKind != JsonValueKind.Array
            || list.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
        { await SendText(c, 400, "Missing app order."); return; }
        apps.Reorder(list.EnumerateArray().Select(e => e.GetString() ?? "").ToList());
        Empty(c);
    }
}
