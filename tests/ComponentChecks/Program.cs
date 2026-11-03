using System;
using System.IO;
using System.Text.Json;
using Luma.Island.Services;

int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }

var directory = Path.Combine(Path.GetFullPath(args.Length > 0 ? args[0] : "work"),
    "component-checks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var path = Path.Combine(directory, "components.json");
var store = new ComponentStore(path);

Check(store.Load() == ComponentSettings.Default, "missing file returns component defaults");
Check(!File.Exists(path), "reading missing file does not create it");
Check(ComponentSettings.Default == new ComponentSettings(true, true, true, true, true, false, false, false, false),
    "only basic components enabled by default");
Check(!ComponentSettings.Default.Charts, "charts disabled in defaults and legacy nine-argument constructor");

var allOff = new ComponentSettings(false, false, false, false, false, false, false, false, false, false);
Check(store.Save(allOff), "all disabled settings save");
Check(new ComponentStore(path).Load() == allOff, "all disabled settings survive fresh store");
var allOn = new ComponentSettings(true, true, true, true, true, true, true, true, true, true);
Check(store.Save(allOn), "all enabled settings save");
Check(new ComponentStore(path).Load() == allOn, "every boolean preserves true through fresh store");
var mixed = new ComponentSettings(false, true, false, true, false, true, false, true, true, true);
Check(store.Save(mixed), "mixed settings save");
Check(new ComponentStore(path).Load() == mixed, "all mixed booleans survive fresh store");
using (var json = JsonDocument.Parse(File.ReadAllText(path)))
{
    Check(json.RootElement.GetProperty("music").ValueKind == JsonValueKind.False, "camelCase boolean properties");
    Check(json.RootElement.GetProperty("fps").ValueKind == JsonValueKind.True, "FPS uses camelCase property");
    Check(json.RootElement.GetProperty("charts").ValueKind == JsonValueKind.True, "charts flag saved as camelCase boolean");
}

File.WriteAllText(path, "{\"music\":false,\"volume\":true,\"time\":false,\"date\":true,\"battery\":false,\"cpu\":true,\"gpu\":false,\"memory\":true,\"fps\":true}");
Check(store.Load() == (mixed with { Charts = false }), "legacy JSON without charts retains settings and disables charts");
File.WriteAllText(path, "{\"time\":false,\"cpu\":true}");
Check(store.Load() == (ComponentSettings.Default with { Time = false, Cpu = true }),
    "partial JSON preserves defaults for missing properties");
File.WriteAllText(path, "{\"music\":false,\"futureComponent\":{\"enabled\":true}}");
Check(store.Load() == (ComponentSettings.Default with { Music = false }), "unknown fields ignored");
File.WriteAllText(path, "{\"MuSiC\":false,\"BATTERY\":false}");
Check(store.Load() == (ComponentSettings.Default with { Music = false, Battery = false }), "property names case insensitive");

foreach (var content in new[]
{
    "{}", "null", "{oops", "[]", "{\"music\":\"false\"}", "{\"volume\":42}", "{\"cpu\":null}"
})
{
    File.WriteAllText(path, content);
    Check(store.Load() == ComponentSettings.Default, "invalid/default JSON: " + content);
}

Check(store.Save(mixed), "prepare existing file for locked write");
byte[] before = File.ReadAllBytes(path);
using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
{
    Check(!store.Save(allOff), "locked destination reports save failure");
    Check(store.Load() == ComponentSettings.Default, "locked read falls back safely");
}
byte[] after = File.ReadAllBytes(path);
Check(before.AsSpan().SequenceEqual(after), "failed replacement preserves old bytes");
Check(new ComponentStore(path).Load() == mixed, "old component choices survive failed save");
Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "failed save leaves no temp files");

Console.WriteLine($"PASS: {checks} component checks. Evidence: {directory}");

namespace Luma.Island
{
    internal static class App { internal static void Log(Exception exception) { } }
}
