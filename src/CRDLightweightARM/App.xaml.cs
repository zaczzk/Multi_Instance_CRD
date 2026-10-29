using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using CRDLightweightARM.Hardware;
using CRDLightweightARM.NativeProtocol;

namespace CRDLightweightARM;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && Array.Exists(e.Args, a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            int code = RunHardwareAndProtocolSelfTest();
            Environment.Exit(code);
            return;
        }

        ProcessJobTracker.Initialize();

        const string appName = "CRDLightweightARM_SingleInstanceMutex";
        _mutex = new Mutex(true, appName, out bool createdNew);

        if (!createdNew)
        {
            // Already running - exit duplicate instance
            MessageBox.Show(
                "Chrome Remote Desktop Lightweight ARM is already running.\nCheck your system tray or active windows.",
                "Already Running",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            Current.Shutdown();
            return;
        }

        base.OnStartup(e);
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private static int RunHardwareAndProtocolSelfTest()
    {
        var sb = new StringBuilder();
        try
        {
            // 1. Verify SnapdragonOryonGovernor
            var gov = SnapdragonOryonGovernor.Instance;
            bool promoted = gov.PromoteMainThreadForLowLatency();
            sb.AppendLine($"[1/5] SnapdragonOryonGovernor: Badge='{gov.HardwareBadge}' | Oryon={gov.IsSnapdragonOryon} | Adreno={gov.IsAdrenoGpu} | MMCSS_Promoted={promoted}");

            // 2. Verify DisplayTopologyDetector
            var topo = DisplayTopologyDetector.DetectTopology();
            var full = DisplayTopologyDetector.GetFullBleedResolution();
            var split = DisplayTopologyDetector.GetCockpitSplitResolution();
            sb.AppendLine($"[2/5] DisplayTopologyDetector: Physical={topo.PhysicalWidth}x{topo.PhysicalHeight} ({topo.AspectRatio}) @ {topo.RefreshRateHz}Hz, {topo.DpiX} DPI | FullBleed={full.Width}x{full.Height} | CockpitSplit={split.Width}x{split.Height}");

            // 3. Verify ChromotingProtobuf Round-Trip (ClientResolution, VideoLayout, VideoControl, USB HID Chords)
            var sync = ChromotingProtobuf.BuildNativeResolutionSyncMessages(full.Width, full.Height, (int)full.Dpi, 0, topo.RefreshRateHz, false);
            var decRes = ControlMessage.Decode(sync.ClientResolutionBytes, false);
            var decLayout = ControlMessage.Decode(sync.VideoLayoutBytes, false);
            var decCtrl = ControlMessage.Decode(sync.VideoControlBytes, false);
            var cadEvents = UsbHidKeycodeMapper.BuildChordEventMessages(UsbHidKeycodeMapper.SystemChord.CtrlAltDel, false);
            sb.AppendLine($"[3/5] ChromotingProtobuf: ClientRes={decRes.ClientResolution?.DipsWidth}x{decRes.ClientResolution?.DipsHeight} | VideoLayout={decLayout.VideoLayout?.VideoTracks[0].Width}x{decLayout.VideoLayout?.VideoTracks[0].Height} @ {decLayout.VideoLayout?.VideoTracks[0].XDpi} DPI | VideoControl_TargetFps={decCtrl.VideoControl?.TargetFramerate} | CAD_Events={cadEvents.Count}");

            // 4. Verify SPAKE2 Curve25519 Handshake & Mutual Verification Hash
            byte[] secret = Spake2Authenticator.DerivePinSharedSecret("host-uuid-snapdragon-x", "849201");
            var alice = new Spake2Authenticator.Session(Spake2Authenticator.Session.SessionRole.Alice, "client@ftl.chromoting.com", "host@ftl.chromoting.com", secret);
            var bob = new Spake2Authenticator.Session(Spake2Authenticator.Session.SessionRole.Bob, "host@ftl.chromoting.com", "client@ftl.chromoting.com", secret);
            byte[] msgA = alice.GenerateMessage();
            byte[] msgB = bob.GenerateMessage();
            byte[] dummyCert = new byte[] { 0x30, 0x82, 0x01, 0x0A, 0x02, 0x01, 0x01 };
            alice.ProcessPeerMessage(msgB, dummyCert);
            bob.ProcessPeerMessage(msgA, dummyCert);
            bool aliceVerifiedBob = alice.VerifyPeerHash(bob.GetOutgoingVerificationHash());
            bool bobVerifiedAlice = bob.VerifyPeerHash(alice.GetOutgoingVerificationHash());
            sb.AppendLine($"[4/5] Spake2Authenticator (Ed25519/Curve25519): AliceVerifiedBob={aliceVerifiedBob} | BobVerifiedAlice={bobVerifiedAlice}");

            // 5. Verify D3D11AdrenoPresenter Hardware Video Device & SwapChain on Adreno X1-45
            var testWin = new Window { Width = 320, Height = 200, WindowStyle = WindowStyle.None, ShowInTaskbar = false, Opacity = 0 };
            var hwnd = new WindowInteropHelper(testWin).EnsureHandle();
            using var presenter = new D3D11AdrenoPresenter();
            presenter.Initialize(hwnd, 320, 200, enableTearing: true, enableWaitableObject: true);
            byte[] nv12Frame = new byte[320 * 200 * 3 / 2];
            presenter.PresentNV12Buffer(nv12Frame, 320, 200, vsync: false);
            sb.AppendLine($"[5/5] D3D11AdrenoPresenter: Initialized={presenter.IsInitialized} | Adapter='{presenter.AdapterDescription}' | IsAdrenoGpu={presenter.IsAdrenoGpu} | TearingSupported={presenter.IsTearingSupported} | NV12_Presented=True");

            gov.RevertMainThreadPromotion();

            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test-report.txt"), sb.ToString());
            return (promoted && aliceVerifiedBob && bobVerifiedAlice && presenter.IsInitialized) ? 0 : 1;
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[ERROR] {ex}");
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test-report.txt"), sb.ToString());
            return 2;
        }
    }
}
