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
                "ROSがエラーに遭遇し、正しく初期化できなかった。 " +
                "Discordから制作者にご連絡ください。.\n\n" + ex,
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
                "Riotクライアントは現在稼働中です。オンライン状態を隠すには、ROSがRiot Clientを起動する必要があります。" +
                "ROSにRiotクライアントとそれによって起動されるゲームを停止させ、適切な設定で再起動させたいですか？",
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
        Trace.WriteLine($"ポートでリッスン中のチャットプロキシ: {port}");

        // Step 2: Find the Riot Client.
        var riotClientPath = Utils.GetRiotClientPath();

        // If the riot client doesn't exist, the user is either severely outdated or has a bugged install.
        if (riotClientPath is null)
        {
            MessageBox.Show(
                "ROSがRiotクライアントへのパスを見つけることができませんでした。通常、Riot Gamesのゲームを一度起動し、ROSを再度起動することで解決します。" +
                "Discord を通じてバグレポートを提出してください。",
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

        Trace.WriteLine($"Riotクライアントをパラメータ付きで起動します:\n{startArgs.Arguments}");
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
            Trace.WriteLine($"元のチャットサーバーの詳細は次のとおりです  {chatHost}:{chatPort}");
        };

        Trace.WriteLine("クライアントがチャットサーバーに接続するのを待っています...");
        var incoming = await listener.AcceptTcpClientAsync();
        Trace.WriteLine("クライアントが接続されました！");

        // Step 6: Connect sockets.
        var sslIncoming = new SslStream(incoming.GetStream());
        var cert = new X509Certificate2(Resources.Certificate);
        await sslIncoming.AuthenticateAsServerAsync(cert);

        if (chatHost is null)
        {
            MessageBox.Show(
                "ROSはRiotのチャットサーバーを見つけることができませんでした。 " +
                "この問題が解決せず、ROSなしで正常にチャットに接続できる場合、" +
                "Discordでバグ報告をしてください。",
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
            Trace.WriteLine("再接続を試みる");
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
                        "チャットサーバーに再接続できません。インターネット接続を確認してください。 " +
                        "この問題が解決せず、ROSなしで正常にチャットに接続できる場合, " +
                        "Discordでバグ報告をしてください。",
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
            Trace.WriteLine("RiotClientの終了を検出.");
            await Task.Delay(3000); // wait for a bit to ensure this is not a relaunch triggered by the RC

            var newProcess = Utils.GetRiotClientProcess();
            if (newProcess is not null)
            {
                Trace.WriteLine("新しいRiotClientプロセスが生成され、終了を監視するようになりました。");
                ListenToRiotClientExit(newProcess);
            }
            else
            {
                Trace.WriteLine("待てど暮らせどRiotClientの起動を確認できませんでした。");
                Environment.Exit(0);
            }
        };
    }
}
