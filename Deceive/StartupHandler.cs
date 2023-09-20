using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Windows.Forms;
using ROS.Properties;

namespace ROS;

internal static class StartupHandler
{
    public static string ROSTitle => "ROS " + Utils.ROSVersion;

    // Arguments are parsed through System.CommandLine.DragonFruit.
    /// <param name="args">The game to be launched, or automatically determined if not passed.</param>
    /// <param name="gamePatchline">The patchline to be used for launching the game.</param>
    /// <param name="riotClientParams">Any extra parameters to be passed to the Riot Client.</param>
    /// <param name="gameParams">Any extra parameters to be passed to the launched game.</param>
    [STAThread]
    public static async Task Main(LaunchGame args = LaunchGame.Auto, string gamePatchline = "live", string? riotClientParams = null, string? gameParams = null)
    {
        AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledException;
        Application.EnableVisualStyles();
        try
        {
            await StartROSAsync(args, gamePatchline, riotClientParams, gameParams);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            // Show some kind of message so that ROS doesn't just disappear.
            MessageBox.Show(
                "ROS���G���[�ɑ������A�������������ł��Ȃ������B " +
                "Discord���琧��҂ɂ��A�����������B.\n\n" + ex,
                ROSTitle,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error,
                MessageBoxDefaultButton.Button1
            );
        }
    }

    /// Actual main function. Wrapped into a separate function so we can catch exceptions.
    private static async Task StartROSAsync(LaunchGame game, string gamePatchline, string? riotClientParams, string? gameParams)
    {
        // Refuse to do anything if the client is already running, unless we're specifically
        // allowing that through League/RC's --allow-multiple-clients.
        if (Utils.IsClientRunning() && !(riotClientParams?.Contains("allow-multiple-clients") ?? false))
        {
            var result = MessageBox.Show(
                "Riot�N���C�A���g�͌��݉ғ����ł��B�I�����C����Ԃ��B���ɂ́AROS��Riot Client���N������K�v������܂��B" +
                "ROS��Riot�N���C�A���g�Ƃ���ɂ���ċN�������Q�[�����~�����A�K�؂Ȑݒ�ōċN�����������ł����H",
                ROSTitle,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1
            );

            if (result is not DialogResult.Yes)
                return;
            Utils.KillProcesses();
            await Task.Delay(2000); // Riot Client takes a while to die
        }

        try
        {
            File.WriteAllText(Path.Combine(Persistence.DataDir, "debug.log"), string.Empty);
            Trace.Listeners.Add(new TextWriterTraceListener(Path.Combine(Persistence.DataDir, "debug.log")));
            Debug.AutoFlush = true;
            Trace.WriteLine(ROSTitle);
        }
        catch
        {
            // ignored; just don't save logs if file is already being accessed
        }

        // Step 0: Check for updates in the background.
        _ = Utils.CheckForUpdatesAsync();

        // Step 1: Open a port for our chat proxy, so we can patch chat port into clientconfig.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Trace.WriteLine($"�|�[�g�Ń��b�X�����̃`���b�g�v���L�V: {port}");

        // Step 2: Find the Riot Client.
        var riotClientPath = Utils.GetRiotClientPath();

        // If the riot client doesn't exist, the user is either severely outdated or has a bugged install.
        if (riotClientPath is null)
        {
            MessageBox.Show(
                "ROS��Riot�N���C�A���g�ւ̃p�X�������邱�Ƃ��ł��܂���ł����B�ʏ�ARiot Games�̃Q�[������x�N�����AROS���ēx�N�����邱�Ƃŉ������܂��B" +
                "Discord ��ʂ��ăo�O���|�[�g���o���Ă��������B",
                ROSTitle,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error,
                MessageBoxDefaultButton.Button1
            );

            return;
        }

        // If launching "auto", use the persisted launch game (which defaults to prompt).
        if (game is LaunchGame.Auto)
            game = Persistence.GetDefaultLaunchGame();

        // If prompt, display dialog.
        if (game is LaunchGame.Prompt)
        {
            new GamePromptForm().ShowDialog();
            game = GamePromptForm.SelectedGame;
        }

        // If we don't have a concrete game by now, the user has cancelled and nothing we can do.
        if (game is LaunchGame.Prompt or LaunchGame.Auto)
            return;

        var launchProduct = game switch
        {
            LaunchGame.LoL => "league_of_legends",
            LaunchGame.LoR => "bacon",
            LaunchGame.VALORANT => "valorant",
            LaunchGame.RiotClient => null,
            var x => throw new Exception("Unexpected LaunchGame: " + x)
        };

        // Step 3: Start proxy web server for clientconfig
        var proxyServer = new ConfigProxy(port);

        // Step 4: Launch Riot Client (+game)
        var startArgs = new ProcessStartInfo { FileName = riotClientPath, Arguments = $"--client-config-url=\"http://127.0.0.1:{proxyServer.ConfigPort}\"" };

        if (launchProduct is not null)
            startArgs.Arguments += $" --launch-product={launchProduct} --launch-patchline={gamePatchline}";

        if (riotClientParams is not null)
            startArgs.Arguments += $" {riotClientParams}";

        if (gameParams is not null)
            startArgs.Arguments += $" -- {gameParams}";

        Trace.WriteLine($"Riot�N���C�A���g���p�����[�^�t���ŋN�����܂�:\n{startArgs.Arguments}");
        var riotClient = Process.Start(startArgs);
        // Kill ROS when Riot Client has exited, so no ghost ROS exists.
        if (riotClient is not null)
        {
            ListenToRiotClientExit(riotClient);
        }

        // Step 5: Get chat server and port for this player by listening to event from ConfigProxy.
        string? chatHost = null;
        var chatPort = 0;
        proxyServer.PatchedChatServer += (_, args) =>
        {
            chatHost = args.ChatHost;
            chatPort = args.ChatPort;
            Trace.WriteLine($"���̃`���b�g�T�[�o�[�̏ڍׂ͎��̂Ƃ���ł�  {chatHost}:{chatPort}");
        };

        Trace.WriteLine("�N���C�A���g���`���b�g�T�[�o�[�ɐڑ�����̂�҂��Ă��܂�...");
        var incoming = await listener.AcceptTcpClientAsync();
        Trace.WriteLine("�N���C�A���g���ڑ�����܂����I");

        // Step 6: Connect sockets.
        var sslIncoming = new SslStream(incoming.GetStream());
        var cert = new X509Certificate2(Resources.Certificate);
        await sslIncoming.AuthenticateAsServerAsync(cert);

        if (chatHost is null)
        {
            MessageBox.Show(
                "ROS��Riot�̃`���b�g�T�[�o�[�������邱�Ƃ��ł��܂���ł����B " +
                "���̖�肪���������AROS�Ȃ��Ő���Ƀ`���b�g�ɐڑ��ł���ꍇ�A" +
                "Discord�Ńo�O�񍐂����Ă��������B",
                ROSTitle,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error,
                MessageBoxDefaultButton.Button1
            );
            return;
        }

        var outgoing = new TcpClient(chatHost, chatPort);
        var sslOutgoing = new SslStream(outgoing.GetStream());
        await sslOutgoing.AuthenticateAsClientAsync(chatHost);

        // Step 7: All sockets are now connected, start tray icon.
        var mainController = new MainController();
        mainController.StartThreads(sslIncoming, sslOutgoing);
        mainController.ConnectionErrored += async (_, _) =>
        {
            Trace.WriteLine("�Đڑ������݂�");
            sslIncoming.Close();
            sslOutgoing.Close();
            incoming.Close();
            outgoing.Close();

            incoming = await listener.AcceptTcpClientAsync();
            sslIncoming = new SslStream(incoming.GetStream());
            await sslIncoming.AuthenticateAsServerAsync(cert);
            while (true)
                try
                {
                    outgoing = new TcpClient(chatHost, chatPort);
                    break;
                }
                catch (SocketException e)
                {
                    Trace.WriteLine(e);
                    var result = MessageBox.Show(
                        "�`���b�g�T�[�o�[�ɍĐڑ��ł��܂���B�C���^�[�l�b�g�ڑ����m�F���Ă��������B " +
                        "���̖�肪���������AROS�Ȃ��Ő���Ƀ`���b�g�ɐڑ��ł���ꍇ, " +
                        "Discord�Ńo�O�񍐂����Ă��������B",
                        ROSTitle,
                        MessageBoxButtons.RetryCancel,
                        MessageBoxIcon.Error,
                        MessageBoxDefaultButton.Button1
                    );
                    if (result == DialogResult.Cancel)
                        Environment.Exit(0);
                }

            sslOutgoing = new SslStream(outgoing.GetStream());
            await sslOutgoing.AuthenticateAsClientAsync(chatHost);
            mainController.StartThreads(sslIncoming, sslOutgoing);
        };
        Application.Run(mainController);
    }

    private static void CurrentDomainOnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Log all unhandled exceptions
        Trace.WriteLine(e.ExceptionObject as Exception);
        Trace.WriteLine(Environment.StackTrace);
    }

    private static void ListenToRiotClientExit(Process riotClientProcess)
    {
        riotClientProcess.EnableRaisingEvents = true;
        riotClientProcess.Exited += async (sender, e) =>
        {
            Trace.WriteLine("RiotClient�̏I�������o.");
            await Task.Delay(3000); // wait for a bit to ensure this is not a relaunch triggered by the RC

            var newProcess = Utils.GetRiotClientProcess();
            if (newProcess is not null)
            {
                Trace.WriteLine("�V����RiotClient�v���Z�X����������A�I�����Ď�����悤�ɂȂ�܂����B");
                ListenToRiotClientExit(newProcess);
            }
            else
            {
                Trace.WriteLine("�҂ĂǕ�点��RiotClient�̋N�����m�F�ł��܂���ł����B");
                Environment.Exit(0);
            }
        };
    }
}
