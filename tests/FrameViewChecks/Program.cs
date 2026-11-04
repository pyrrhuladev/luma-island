using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Luma.Island.Services;

int checks = 0;
void Check(bool value, string description) { if (!value) throw new InvalidOperationException(description); checks++; }
byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
var frame = new FrameViewFrame(123456789, 99, 0xABCDEF, 5.25, 4.75);
const string row = "99,0xABCDEF,123456789,5.25,4.75,0\n";
Check(FrameViewCsvDecoder.TryParse(Bytes(row.TrimEnd()), out var decoded) && decoded == frame, "Hex swapchain and exact QPC/frame intervals decoded");
Check(FrameViewCsvDecoder.TryParse(Bytes("99,11259375,123456789,5.25,4.75,NA"), out decoded) && decoded == frame,
    "Decimal swapchain and unused unavailable latency do not remove valid frame timing");
Check(FrameViewCsvDecoder.TryParse(Bytes(" 99 , 0XABCDEF , 123456789 , 5.25 , 4.75 ,0\r"), out decoded) && decoded == frame, "Whitespace and CRLF are tolerated");
var savedCulture = CultureInfo.CurrentCulture;
try
{
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
    Check(FrameViewCsvDecoder.TryParse(Bytes(row.TrimEnd()), out decoded) && decoded == frame, "CSV uses invariant decimals in Russian locale");
}
finally { CultureInfo.CurrentCulture = savedCulture; }
foreach (string malformed in new[] {
    "", "99,1,123,5,4", "99,1,123,5,4,0,extra", "0,1,123,5,4,0", "4294967296,1,123,5,4,0", "-1,1,123,5,4,0",
    "99,0x,123,5,4,0", "99,0xGG,123,5,4,0", "99,1,0,5,4,0", "99,1,9223372036854775808,5,4,0",
    "99,1,123,NaN,4,0", "99,1,123,Infinity,4,0", "99,1,123,-1,4,0", "99,1,123,60001,4,0", "99,1,123,5,-1,0", "99,1,123,5,NaN,0",
    "99,1,123,5oops,4,0", "99,1,123,5,4x,0", "99,1,123,5,4,0\0", "99,1x,123,5,4,0", "99,1,123x,5,4,0"
})
{
    bool parsed = FrameViewCsvDecoder.TryParse(Bytes(malformed), out _);
    // Latency is intentionally not parsed. Its bytes cannot affect the timing columns.
    bool unusedLatency = malformed == "99,1,123,5,4,0\0";
    Check(parsed == unusedLatency, "Malformed timing/header field is rejected: " + malformed.Replace('\0', '?'));
}
Check(FrameViewCsvDecoder.TryParse(Bytes("99,1,123,0,0,0"), out var zero) && zero.Milliseconds == 0,
    "A real first-frame zero is preserved for caller availability policy rather than fabricated");
Check(FrameViewCsvDecoder.TryParse(Bytes("99,0,123,5,4,0"), out var zeroChain) && zeroChain.SwapChain == 0,
    "A provider's zero swapchain identifier remains usable with the real process/timestamp");
Check(FrameViewCsvDecoder.TryParse(Bytes("99,1,123,60000,60000,0"), out _), "Documented finite timing ceiling is inclusive");
for (int boundary = 0; boundary <= Bytes(row).Length; boundary++)
{
    var parser = new FrameViewCsvDecoder(); var list = new List<FrameViewFrame>(); bool gap = false; byte[] all = Bytes(row);
    parser.Feed(all.AsSpan(0, boundary), list, ref gap, 10);
    parser.Feed(all.AsSpan(boundary), list, ref gap, 10);
    Check(list.Count == 1 && list[0] == frame && !gap, "Split at every byte preserves a partial row: " + boundary);
}
{
    var parser = new FrameViewCsvDecoder(); var list = new List<FrameViewFrame>(); bool gap = false;
    foreach (byte value in Bytes("\uFEFFProcessId,SwapChainAddress,QpcTime,MsBetweenPresents,MsBetweenDisplayChange,MsPCLatency\r\n\r\n" + row))
        parser.Feed(new[] { value }, list, ref gap, 10);
    Check(list.Count == 1 && list[0] == frame && !gap, "BOM/header/blank line and bytewise feeds retain only actual samples");
}
{
    var parser = new FrameViewCsvDecoder(); var list = new List<FrameViewFrame>(); bool gap = false;
    parser.Feed(Bytes(row + "bad row\n" + row.Replace("123456789", "123456790")), list, ref gap, 10);
    Check(gap && list.Count == 1 && list[0].Timestamp == 123456790, "A malformed row marks a gap and removes all pre-loss samples from that drain");
}
{
    var parser = new FrameViewCsvDecoder(); var list = new List<FrameViewFrame>(); bool gap = false;
    parser.Feed(Bytes(row + new string('X', FrameViewCsvDecoder.MaximumLineBytes + 800)), list, ref gap, 10);
    Check(gap && list.Count == 0, "Overlong unterminated line is discarded immediately with bounded storage");
    parser.Feed(Bytes("\n" + row), list, ref gap, 10);
    Check(list.Count == 1 && list[0] == frame, "Parser resynchronizes at newline after an oversized line");
}
{
    var parser = new FrameViewCsvDecoder(); var list = new List<FrameViewFrame>(); bool gap = false;
    parser.Feed(Bytes(row + row + row.Replace("123456789", "123456790")), list, ref gap, 2);
    Check(gap && list.Count == 1 && list[0].Timestamp == 123456790, "Frame budget retains only post-overflow samples and explicitly marks loss");
    parser.Feed(Bytes("99,0x"), list, ref gap, 2); parser.Reset(); list.Clear(); gap = false;
    parser.Feed(Bytes(row), list, ref gap, 2);
    Check(list.Count == 1 && !gap, "Reset cannot join data from separate streams");
}
Check(Marshal.SizeOf<FrameViewSource.NativeMetric>() == 56, "Both native FPS metric structures retain the verified x64 ABI size");
foreach (var expected in new[] { ("BaseVersion",0),("Timestamp",8),("Version",16),("FpsVersion",24),("ProcessId",32),("SwapChain",40),("Fps",48) })
    Check(Marshal.OffsetOf<FrameViewSource.NativeMetric>(expected.Item1).ToInt32() == expected.Item2, "Verified SDK field offset: " + expected.Item1);
var metric = new FrameViewSource.NativeMetric { BaseVersion=0x10010,Version=0x10038,FpsVersion=0x10020,Timestamp=1234,ProcessId=99,SwapChain=0xABC,Fps=22.5 };
Check(FrameViewSource.DecodeMetric(metric) == new FrameViewMetric(1234,99,0xABC,22.5), "Native 1% Low uses the FPS metric ABI without changing values");
foreach (int field in new[]{0,1,2})
{
    var bad=metric; if(field==0)bad.BaseVersion++;if(field==1)bad.Version++;if(field==2)bad.FpsVersion++;
    bool rejected=false;try{FrameViewSource.DecodeMetric(bad);}catch(InvalidDataException){rejected=true;}
    Check(rejected,"Unknown native structure version is rejected: "+field);
}
Check(FrameViewSource.LowMetric == 0x29CCDD99 && FrameViewSource.LowMetric != 0xCA3E085A, "1% Low is the SDK low metric, not its 99th percentile metric");
Check(FrameViewSource.MaximumBytesPerDrain == 262144 && FrameViewSource.MaximumFramesPerDrain == 4096, "Transport and output work are bounded independently");
Check(FrameViewSource.IsReadablePipeChunk(true, 20, 64, 0), "A successful byte-pipe chunk is accepted");
Check(FrameViewSource.IsReadablePipeChunk(false, 20, 64, 234), "ERROR_MORE_DATA preserves a valid partial message-mode chunk");
Check(!FrameViewSource.IsReadablePipeChunk(false, 20, 64, 109) && !FrameViewSource.IsReadablePipeChunk(false, 0, 64, 234), "Broken pipes and empty partial chunks are not parsed as data");
Check(!FrameViewSource.IsReadablePipeChunk(true, 65, 64, 0), "An impossible native byte count cannot overrun the managed buffer");
foreach (var mode in new[] { "all", "no-low", "no-frames", "no-combination", "average-only" })
{
    uint[]? final = null; int calls=0;
    uint Enable(uint[] metrics)
    {
        calls++; bool low=metrics.Contains(FrameViewSource.LowMetric), frames=metrics.Contains(FrameViewSource.FrameMetric);
        uint result = mode switch { "no-low" when low => 8, "no-frames" when frames => 8, "no-combination" when low&&frames => 5,
            "average-only" when low||frames => 8, _ => 0 };
        if(result==0)final=(uint[])metrics.Clone();
        return result;
    }
    var supported=FrameViewSource.EnableCapabilities(true,true,Enable);
    var expected=mode switch { "no-low" => new FrameViewSource.Capabilities(false,true), "no-frames" or "no-combination" => new(true,false),
        "average-only" => new(false,false), _ => new(true,true) };
    Check(supported==expected,"Optional SDK capability fallback preserves every usable metric: "+mode);
    Check(final is not null&&final.Contains(FrameViewSource.AverageMetric)&&final.Contains(FrameViewSource.LowMetric)==supported.Low&&
        final.Contains(FrameViewSource.FrameMetric)==supported.Frames,"The final SDK set matches published capabilities: "+mode);
    Check(mode!="all"||calls==1,"A supported SDK needs only one combined enable call");
}
bool failedAverage=false;try{FrameViewSource.EnableCapabilities(true,true,_=>7);}catch(InvalidOperationException){failedAverage=true;}
Check(failedAverage,"Failure of the required average source remains visible instead of inventing optional values");
string output = Path.GetFullPath(args.Length > 0 ? args[0] : "work/FrameViewChecks"); Directory.CreateDirectory(output);
File.WriteAllText(Path.Combine(output,"verification.json"),JsonSerializer.Serialize(new { passed=true,checks,scope="Pure CSV and ABI tests only; no SDK, service, driver, game or real preferences accessed." },new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS: {checks} FrameView CSV and ABI checks.");

namespace Luma.Island { internal static class App { internal static void Log(Exception exception) { } } }
