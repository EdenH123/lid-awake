using LidAwake;
using NAudio.CoreAudioApi;
using Xunit;
using Xunit.Abstractions;

// The real Media Foundation and WASAPI on this machine. A CI runner usually has no camera,
// microphone or speakers, so these never fail for a missing device: they say what they found
// (lines starting "CAMERA-REPORT:"), and check the Media Foundation calls that need no device.
// Nothing here opens a camera, so the webcam light never comes on while the tests run.
public class CameraDeviceTests
{
    readonly ITestOutputHelper output;

    public CameraDeviceTests(ITestOutputHelper output) => this.output = output;

    void Report(string line)
    {
        output.WriteLine("CAMERA-REPORT: " + line);
        Console.WriteLine("CAMERA-REPORT: " + line);
    }

    [Fact]
    public void ReportWhatThisMachineHas()
    {
        bool mf = Mf.Start();
        Report($"Media Foundation: {(mf ? "yes" : "no")}");
        if (mf)
        {
            var cameras = CameraHardware.Cameras();
            Report($"cameras: {cameras.Count}");
            foreach (var c in CameraChoice.Order(cameras)) Report($"camera: {c.Name} ({c.Place})");
        }
        Report($"microphones: {Endpoints(DataFlow.Capture)}");
        Report($"speakers: {Endpoints(DataFlow.Render)}");
    }

    static string Endpoints(DataFlow flow)
    {
        try { return new MMDeviceEnumerator().EnumerateAudioEndPoints(flow, DeviceState.Active).Count.ToString(); }
        catch (Exception e) { return "unknown (" + e.GetType().Name + ")"; }
    }

    [Fact]
    public void MediaTypeAttributesGoInAndComeBack()
    {
        if (!Mf.Start()) { Report("Media Foundation missing: attribute check skipped"); return; }
        Assert.True(Mf.MFCreateMediaType(out var type) >= 0);
        try
        {
            Assert.True(Mf.SetGuid(type, Mf.MajorType, Mf.Video) >= 0);
            Assert.True(Mf.SetGuid(type, Mf.Subtype, Mf.Nv12) >= 0);
            Assert.True(Mf.SetUInt64(type, Mf.FrameSize, (1280UL << 32) | 720) >= 0);
            Assert.True(Mf.SetUInt64(type, Mf.FrameRate, (30000UL << 32) | 1001) >= 0);
            Assert.True(Mf.SetUInt32(type, Mf.HardwareTransforms, 7) >= 0);
            Assert.Equal(Mf.Video, Mf.GetGuid(type, Mf.MajorType));
            Assert.Equal(7u, Mf.GetUInt32(type, Mf.HardwareTransforms));
            Assert.Null(Mf.GetUInt32(type, Mf.AdvancedVideoProcessing));
            Assert.Null(Mf.GetString(type, Mf.FriendlyName));
            var f = Mf.Describe(type);
            Assert.Equal(new VideoFormat(Mf.Nv12, 1280, 720, 30000 / 1001.0), f);
        }
        finally { Mf.Release(type); }
    }

    [Fact]
    public void ASamplesBytesComeBackInOnePiece()
    {
        if (!Mf.Start()) { Report("Media Foundation missing: sample check skipped"); return; }
        var bytes = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        var sample = Mf.MakeSample(bytes);
        Assert.NotEqual(IntPtr.Zero, sample);
        try { Assert.Equal(bytes, Mf.SampleBytes(sample)); }
        finally { Mf.Release(sample); }
    }

    [Fact]
    public void WithoutACameraTheProblemIsSaid()
    {
        if (!Mf.Start()) return;
        if (CameraHardware.Cameras().Count > 0) { Report("a camera is present: the no-camera check is skipped (it would turn it on)"); return; }
        var capture = new CameraHardware().Open(new NullSink(), out var problem);
        Assert.Null(capture);
        Assert.Equal(CameraTexts.NoCamera, problem);
        var closed = new CameraHardware(() => true).Open(new NullSink(), out var lidProblem);
        Assert.Null(closed);
        Assert.Equal(CameraTexts.LidClosed, lidProblem);
    }

    sealed class NullSink : ICameraSink
    {
        public void Frame(CameraFrame frame) { }
        public void Sound(ReadOnlySpan<float> firstChannel, int rate) { }
        public void Failed(string text) { }
    }

    [Fact]
    public void TheSpeakersOpenOrSayThereAreNone()
    {
        // No speakers on the runner: null, never a throw. With speakers: it plays silence, then stops.
        var player = new TalkPlayer(WasapiTalkOutput.Open);
        bool played = player.Play(Enumerable.Repeat((byte)0xFF, 160).ToArray());  // 0xFF is silence
        Report($"talk back output: {(played ? "opened" : "none")}");
        player.Stop();
        Assert.False(player.Playing);
    }
}
