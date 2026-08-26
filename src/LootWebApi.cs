using System.IO;
using System.Text;
using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Servers.Http;

namespace Leaves_LootFuckery;

// The LootFuckery web UI. Two independent pages, each served as a static file, plus a small JSON API
// they fetch from:
//   /lootfuckery/ui/dump      - the large-scale simulated-dump viewer (the old standalone lootviewer,
//                               now able to load the latest dumps straight off the server)
//   /lootfuckery/ui/history   - the real-raid capture list (see RealRunCapture)
//   /lootfuckery/api/dumps    - list the loot_<map>_<timestamp>.json files in output/
//   /lootfuckery/api/dump     - one dump file's contents        (?file=<name>)
//   /lootfuckery/api/runs     - the captured-run index, newest first
//   /lootfuckery/api/run      - one captured run's full contents (?file=<map>/<name>)
//   /lootfuckery/api/maps     - the DynamicMaps map catalogue (folder id + its .jsonc path)
//   /lootfuckery/maps/<path>  - a single map asset off assets/maps (.svg / .jsonc / .json only;
//                               licence .md files are deliberately NOT served, see HandleMapFile)
//
// Registered automatically by being [Injectable]; SPT routes any request we CanHandle here. The whole
// "/lootfuckery/" prefix is ours, so this never shadows an SPT route. Browser-facing and read-only -
// no game session is involved, everything is served off disk.
[Injectable]
public class LootWebApi(ModHelper modHelper) : IHttpListener, IOnLoad
{
    private const string Prefix = "/lootfuckery/";

    // Publish our routes to the suite-wide Dev directory (served at /webui/dev/routes). Utils.Instance is set well before any OnLoad.
    public Task OnLoad()
    {
        var routes = new List<Leaves.RouteInfo>
        {
            new Leaves.RouteInfo("GET", "/lootfuckery/ui/dump", "Simulated-dump viewer.", "ui"),
            new Leaves.RouteInfo("GET", "/lootfuckery/ui/history", "Real-raid capture list.", "ui"),
            new Leaves.RouteInfo("GET", "/lootfuckery/api/dumps", "List the loot_<map>_<timestamp>.json dump files, newest first.", "api"),
            new Leaves.RouteInfo("GET", "/lootfuckery/api/dump?file=", "One dump file's contents.", "api"),
            new Leaves.RouteInfo("GET", "/lootfuckery/api/runs", "Captured-run index, newest first.", "api"),
            new Leaves.RouteInfo("GET", "/lootfuckery/api/run?file=", "One captured run's full contents (file = <map>/<name>).", "api"),
            new Leaves.RouteInfo("GET", "/lootfuckery/api/maps", "DynamicMaps map catalogue (folder id + its .jsonc path).", "api"),
            new Leaves.RouteInfo("GET", "/lootfuckery/maps/{path}", "A single map asset off assets/maps (.svg/.jsonc/.json only).", "internal"),
        };
        Leaves.Utils.Instance.RegisterRoutes("Loot Fuckery", routes);
        return Task.CompletedTask;
    }

    private readonly string modPath = modHelper.GetAbsolutePathToModFolder(System.Reflection.Assembly.GetExecutingAssembly());

    // Files are read fresh on each request (these pages are hit rarely) so edits to the deployed HTML
    // show up without a rebuild.
    private string UiDir => System.IO.Path.Combine(modPath, "assets", "ui");
    private string OutputDir => System.IO.Path.Combine(modPath, "output");
    private string HistoryDir => System.IO.Path.Combine(modPath, "output", "history");
    private string MapsDir => System.IO.Path.Combine(modPath, "assets", "maps");

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        return Leaves.HttpUtils.HasPrefix(context, Prefix);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        string path = context.Request.Path.Value ?? "";
        string method = context.Request.Method;

        try
        {
            if (method == "GET" && path.EndsWith("/ui/dump", StringComparison.OrdinalIgnoreCase))
            {
                await Leaves.HttpUtils.ServeFile(context, System.IO.Path.Combine(UiDir, "dump.html"), "text/html; charset=utf-8");
            }
            else if (method == "GET" && path.EndsWith("/ui/history", StringComparison.OrdinalIgnoreCase))
            {
                await Leaves.HttpUtils.ServeFile(context, System.IO.Path.Combine(UiDir, "history.html"), "text/html; charset=utf-8");
            }
            else if (method == "GET" && path.EndsWith("/api/dumps", StringComparison.OrdinalIgnoreCase))
            {
                await HandleDumpsList(context);
            }
            else if (method == "GET" && path.EndsWith("/api/dump", StringComparison.OrdinalIgnoreCase))
            {
                await HandleDumpFile(context);
            }
            else if (method == "GET" && path.EndsWith("/api/runs", StringComparison.OrdinalIgnoreCase))
            {
                await HandleRunsList(context);
            }
            else if (method == "GET" && path.EndsWith("/api/run", StringComparison.OrdinalIgnoreCase))
            {
                await HandleRunFile(context);
            }
            else if (method == "GET" && path.EndsWith("/api/maps", StringComparison.OrdinalIgnoreCase))
            {
                await HandleMapsList(context);
            }
            else if (method == "GET" && path.Contains("/maps/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleMapFile(context, path);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync($"Unknown LootFuckery resource: {path}");
            }
        }
        catch (Exception ex)
        {
            Leaves.Utils.Instance.printColor($"[LootFuckery] Web API error on {method} {path}: {ex.Message}", LogTextColor.Red);
            await Leaves.HttpUtils.WriteJson(context, StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    // GET /lootfuckery/api/dumps -> [{ file, map, generated }] newest first, for the dump viewer's picker.
    // The mass-dump files are named loot_<map>_-_<timestamp>.json (see LootFuckery.WriteResult); map and
    // time are derived from the name, no need to open the (large) files just to list them.
    private async Task HandleDumpsList(HttpContext context)
    {
        var dumps = new List<object>();
        if (Directory.Exists(OutputDir))
        {
            var files = new List<string>(Directory.GetFiles(OutputDir, "loot_*.json"));
            // Newest first by write time.
            files.Sort((a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
            foreach (var full in files)
            {
                string name = System.IO.Path.GetFileName(full);
                ParseDumpName(name, out string map, out string generated);
                dumps.Add(new { file = name, map, generated });
            }
        }
        await Leaves.HttpUtils.WriteJson(context, StatusCodes.Status200OK, dumps);
    }

    // Splits loot_<map>_-_<timestamp>.json into its map and timestamp parts. Anything unexpected falls
    // back to the raw name so the picker still shows the file.
    private static void ParseDumpName(string fileName, out string map, out string generated)
    {
        map = fileName;
        generated = "";
        string core = fileName;
        if (core.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            core = core.Substring(0, core.Length - 5);
        }
        if (core.StartsWith("loot_", StringComparison.OrdinalIgnoreCase))
        {
            core = core.Substring(5);
        }
        int split = core.IndexOf("_-_", StringComparison.Ordinal);
        if (split >= 0)
        {
            map = core.Substring(0, split);
            generated = core.Substring(split + 3);
        }
        else
        {
            map = core;
        }
    }

    // GET /lootfuckery/api/dump?file=<name> -> the raw dump JSON. Filename only (no path), must resolve
    // inside output/ - no directory traversal.
    private async Task HandleDumpFile(HttpContext context)
    {
        string requested = context.Request.Query["file"].ToString();
        string safeName = System.IO.Path.GetFileName(requested); // strips any path component
        if (string.IsNullOrEmpty(safeName) || !safeName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            await Leaves.HttpUtils.WriteJson(context, StatusCodes.Status400BadRequest, new { error = "Bad or missing 'file'." });
            return;
        }
        string full = System.IO.Path.Combine(OutputDir, safeName);
        if (!File.Exists(full))
        {
            await Leaves.HttpUtils.WriteJson(context, StatusCodes.Status404NotFound, new { error = $"No dump '{safeName}'." });
            return;
        }
        await Leaves.HttpUtils.ServeRawJson(context, full);
    }

    // GET /lootfuckery/api/runs -> the captured-run index as a JSON array, newest first. The index is
    // one JSON object per line (index.jsonl), appended in capture order, so reverse it for newest-first.
    private async Task HandleRunsList(HttpContext context)
    {
        string indexFile = System.IO.Path.Combine(HistoryDir, "index.jsonl");
        var lines = new List<string>();
        if (File.Exists(indexFile))
        {
            foreach (var line in await File.ReadAllLinesAsync(indexFile))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line.Trim());
                }
            }
        }

        var builder = new StringBuilder();
        builder.Append('[');
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            builder.Append(lines[i]);
            if (i > 0)
            {
                builder.Append(',');
            }
        }
        builder.Append(']');

        await Leaves.HttpUtils.WriteJsonRaw(context, StatusCodes.Status200OK, builder.ToString());
    }

    // GET /lootfuckery/api/run?file=<map>/<name> -> one captured run's full JSON. The path may contain a
    // single map subfolder, so we can't just take the filename: instead resolve it and confirm it stays
    // inside output/history/ (blocks ../ traversal) and is a .json.
    private async Task HandleRunFile(HttpContext context)
    {
        string requested = context.Request.Query["file"].ToString();
        if (string.IsNullOrEmpty(requested) || !requested.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            await Leaves.HttpUtils.WriteJson(context, StatusCodes.Status400BadRequest, new { error = "Bad or missing 'file'." });
            return;
        }

        // The path may contain a single map subfolder, so we can't just take the filename - resolve it
        // and confirm it stays inside output/history/ (blocks ../ traversal).
        if (!Leaves.HttpUtils.TryResolveUnderRoot(HistoryDir, requested, out string full))
        {
            await Leaves.HttpUtils.WriteJson(context, StatusCodes.Status400BadRequest, new { error = "Invalid 'file' path." });
            return;
        }
        if (!File.Exists(full))
        {
            await Leaves.HttpUtils.WriteJson(context, StatusCodes.Status404NotFound, new { error = "No such run." });
            return;
        }
        await Leaves.HttpUtils.ServeRawJson(context, full);
    }

    // GET /lootfuckery/api/maps -> [{ id, config }] one entry per map folder under assets/maps, where
    // id is the folder name (e.g. "Customs_TarkovDev") and config is the URL of its .jsonc data file
    // (relative to /lootfuckery/maps/). The UI fetches that jsonc, which in turn names its layer SVGs.
    // We only enumerate folders that actually contain a .jsonc, so a stray folder can't break the picker.
    private async Task HandleMapsList(HttpContext context)
    {
        var maps = new List<object>();
        if (Directory.Exists(MapsDir))
        {
            var dirs = new List<string>(Directory.GetDirectories(MapsDir));
            dirs.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs)
            {
                string id = System.IO.Path.GetFileName(dir);
                var jsoncFiles = new List<string>(Directory.GetFiles(dir, "*.jsonc"));
                if (jsoncFiles.Count == 0)
                {
                    continue;
                }
                string jsoncName = System.IO.Path.GetFileName(jsoncFiles[0]);
                maps.Add(new { id, config = $"{id}/{jsoncName}" });
            }
        }
        await Leaves.HttpUtils.WriteJson(context, StatusCodes.Status200OK, maps);
    }

    // GET /lootfuckery/maps/<relative path> -> a single asset streamed off assets/maps.
    //
    // Two guards, both deliberate:
    //   1. Path traversal - the resolved file must stay inside assets/maps (blocks ../ escapes), same
    //      pattern as HandleRunFile.
    //   2. Extension allowlist - only .svg / .jsonc / .json are ever served. This is what keeps the
    //      Shebuka/acidphantasm LICENSE .md files (which the licence requires we ship on disk) invisible
    //      to browsers: they're present in the folders, but any request for a .md just 404s.
    private async Task HandleMapFile(HttpContext context, string path)
    {
        int marker = path.IndexOf("/maps/", StringComparison.OrdinalIgnoreCase);
        string relative = path.Substring(marker + "/maps/".Length);
        relative = Uri.UnescapeDataString(relative);

        string ext = System.IO.Path.GetExtension(relative).ToLowerInvariant();
        string? contentType = MapAssetContentType(ext);
        if (contentType == null)
        {
            // Not an asset we expose (e.g. a .md licence file) - treat as absent rather than forbidden,
            // so we never even confirm the licence files are there.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Not found.");
            return;
        }

        // Must stay within assets/maps - reject anything that climbed out with ../
        if (!Leaves.HttpUtils.TryResolveUnderRoot(MapsDir, relative, out string full))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Invalid path.");
            return;
        }
        if (!File.Exists(full))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Not found.");
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = contentType;
        await context.Response.WriteAsync(await File.ReadAllTextAsync(full));
    }

    // The allowlist of map-asset extensions we serve, mapped to their content type. Anything not listed
    // here (licence .md, .txt, etc.) returns null and is refused by HandleMapFile.
    private static string? MapAssetContentType(string ext)
    {
        if (ext == ".svg")
        {
            return "image/svg+xml; charset=utf-8";
        }
        if (ext == ".jsonc" || ext == ".json")
        {
            // .jsonc carries // comments, so it isn't strictly application/json, but browsers fetch()ing
            // it as text don't care and callers strip comments themselves.
            return "application/json; charset=utf-8";
        }
        return null;
    }

}
