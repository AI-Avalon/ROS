using System;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;
using ROS.Properties;

namespace ROS;

internal class MainController : ApplicationContext
{
    internal MainController()
    {
        TrayIcon = new NotifyIcon
        {
            Icon = Resources.ROSIcon,
            Visible = true,
            BalloonTipTitle = StartupHandler.ROSTitle,
            BalloonTipText = "ROSは現在、あなたのステータスを隠しています。トレイアイコンを右クリックすると、その他のオプションが表示されます。"
        };
        TrayIcon.ShowBalloonTip(5000);

        LoadStatus();
        UpdateTray();
    }

    private NotifyIcon TrayIcon { get; }
    private bool Enabled { get; set; } = true;
    private string Status { get; set; } = null!;
    private string StatusFile { get; } = Path.Combine(Persistence.DataDir, "status");
    private bool ConnectToMuc { get; set; } = true;
    private bool InsertedFakePlayer { get; set; }
    private bool SentFakePlayerPresence { get; set; }
    private bool SentIntroductionText { get; set; }
    private string? ValorantVersion { get; set; }

    private SslStream Incoming { get; set; } = null!;
    private SslStream Outgoing { get; set; } = null!;
    private bool Connected { get; set; }
    private string LastPresence { get; set; } = null!; // we resend this if the state changes

    private ToolStripMenuItem EnabledMenuItem { get; set; } = null!;
    private ToolStripMenuItem ChatStatus { get; set; } = null!;
    private ToolStripMenuItem OfflineStatus { get; set; } = null!;
    private ToolStripMenuItem MobileStatus { get; set; } = null!;

    internal event EventHandler? ConnectionErrored;

    private void UpdateTray()
    {
        var aboutMenuItem = new ToolStripMenuItem(StartupHandler.ROSTitle) { Enabled = false };

        EnabledMenuItem = new ToolStripMenuItem("Enabled", null, async (_, _) =>
        {
            Enabled = !Enabled;
            await UpdateStatusAsync(Enabled ? Status : "chat");
            await SendMessageFromFakePlayerAsync(Enabled ? "ROSが有効になりました。" : "ROSは無効になりました。");
            UpdateTray();
        }) { Checked = Enabled };

        var mucMenuItem = new ToolStripMenuItem("Enable lobby chat", null, (_, _) =>
        {
            ConnectToMuc = !ConnectToMuc;
            UpdateTray();
        }) { Checked = ConnectToMuc };

        ChatStatus = new ToolStripMenuItem("Online", null, async (_, _) =>
        {
            await UpdateStatusAsync(Status = "chat");
            Enabled = true;
            UpdateTray();
        }) { Checked = Status.Equals("chat") };

        OfflineStatus = new ToolStripMenuItem("Offline", null, async (_, _) =>
        {
            await UpdateStatusAsync(Status = "offline");
            Enabled = true;
            UpdateTray();
        }) { Checked = Status.Equals("offline") };

        MobileStatus = new ToolStripMenuItem("Mobile", null, async (_, _) =>
        {
            await UpdateStatusAsync(Status = "mobile");
            Enabled = true;
            UpdateTray();
        }) { Checked = Status.Equals("mobile") };

        var typeMenuItem = new ToolStripMenuItem("Status Type", null, ChatStatus, OfflineStatus, MobileStatus);

        var restartWithDifferentGameItem = new ToolStripMenuItem("再起動し、別のゲームを起動する。", null, (_, _) =>
        {
            var result = MessageBox.Show(
                "ROSを再起動して別のゲームを起動しますか？関連するゲームが起動している場合は、それも停止します。",
                StartupHandler.ROSTitle,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1
            );

            if (result is not DialogResult.Yes)
                return;

            Utils.KillProcesses();
            Thread.Sleep(2000);

            Persistence.SetDefaultLaunchGame(LaunchGame.Prompt);
            Process.Start(Application.ExecutablePath);
            Environment.Exit(0);
        });

        var quitMenuItem = new ToolStripMenuItem("Quit", null, (_, _) =>
        {
            var result = MessageBox.Show(
                "本当にROSを停止しますか？関連するゲームも停止します。",
                StartupHandler.ROSTitle,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1
            );

            if (result is not DialogResult.Yes)
                return;

            Utils.KillProcesses();
            SaveStatus();
            Application.Exit();
        });

        TrayIcon.ContextMenuStrip = new ContextMenuStrip();

#if DEBUG
        var closeIn = new ToolStripMenuItem("Close incoming", null, (_, _) => { Incoming.Close(); });
        var closeOut = new ToolStripMenuItem("Close outgoing", null, (_, _) => { Outgoing.Close(); });
        var createFakePlayer = new ToolStripMenuItem("Resend fake player", null, async (_, _) => { await SendFakePlayerPresenceAsync(); });
        var sendTestMsg = new ToolStripMenuItem("Send message", null, async (_, _) => { await SendMessageFromFakePlayerAsync("Test"); });

        TrayIcon.ContextMenuStrip.Items.AddRange(new ToolStripItem[]
        {
            aboutMenuItem, EnabledMenuItem, typeMenuItem, mucMenuItem, closeIn, closeOut, createFakePlayer, sendTestMsg, restartWithDifferentGameItem, quitMenuItem
        });
#else
        TrayIcon.ContextMenuStrip.Items.AddRange(new ToolStripItem[] { aboutMenuItem, EnabledMenuItem, typeMenuItem, mucMenuItem, restartWithDifferentGameItem, quitMenuItem });
#endif
    }

    public void StartThreads(SslStream incoming, SslStream outgoing)
    {
        Incoming = incoming;
        Outgoing = outgoing;
        Connected = true;
        InsertedFakePlayer = false;
        SentFakePlayerPresence = false;

        Task.Run(IncomingLoopAsync);
        Task.Run(OutgoingLoopAsync);
    }

    private async Task IncomingLoopAsync()
    {
        try
        {
            int byteCount;
            var bytes = new byte[8192];

            do
            {
                byteCount = await Incoming.ReadAsync(bytes, 0, bytes.Length);

                var content = Encoding.UTF8.GetString(bytes, 0, byteCount);

                // If this is possibly a presence stanza, rewrite it.
                if (content.Contains("<presence") && Enabled)
                {
                    Trace.WriteLine("<!--RC TO SERVER ORIGINAL-->" + content);
                    await PossiblyRewriteAndResendPresenceAsync(content, Status);
                }
                else if (content.Contains("41c322a1-b328-495b-a004-5ccd3e45eae8@eu1.pvp.net"))
                {
                    if (content.ToLower().Contains("offline"))
                    {
                        if (!Enabled)
                            await SendMessageFromFakePlayerAsync("ROSが有効になった。");
                        OfflineStatus.PerformClick();
                    }
                    else if (content.ToLower().Contains("mobile"))
                    {
                        if (!Enabled)
                            await SendMessageFromFakePlayerAsync("ROSが有効になった。");
                        MobileStatus.PerformClick();
                    }
                    else if (content.ToLower().Contains("online"))
                    {
                        if (!Enabled)
                            await SendMessageFromFakePlayerAsync("ROSが有効になった。");
                        ChatStatus.PerformClick();
                    }
                    else if (content.ToLower().Contains("enable"))
                    {
                        if (Enabled)
                            await SendMessageFromFakePlayerAsync("ROSはすでに有効になっている。");
                        else
                            EnabledMenuItem.PerformClick();
                    }
                    else if (content.ToLower().Contains("disable"))
                    {
                        if (!Enabled)
                            await SendMessageFromFakePlayerAsync("ROSはすでに無効になっている。");
                        else
                            EnabledMenuItem.PerformClick();
                    }
                    else if (content.ToLower().Contains("status"))
                    {
                        if (Status == "chat")
                            await SendMessageFromFakePlayerAsync("あなたはオンラインに登場する。");
                        else
                            await SendMessageFromFakePlayerAsync("今あなたは " + Status + ".");
                    }
                    else if (content.ToLower().Contains("help"))
                    {
                        await SendMessageFromFakePlayerAsync("以下のメッセージを送信することで、ROSの設定を素早く変更することができます: online/offline/mobile/enable/disable/status");
                    }

                    //Don't send anything involving our fake user to chat servers
                    Trace.WriteLine("<!--RC TO SERVER REMOVED-->" + content);
                }
                else
                {
                    await Outgoing.WriteAsync(bytes, 0, byteCount);
                    Trace.WriteLine("<!--RC TO SERVER-->" + content);
                }

                if (InsertedFakePlayer && !SentFakePlayerPresence)
                    await SendFakePlayerPresenceAsync();

                if (!SentIntroductionText)
                    await SendIntroductionTextAsync();
            } while (byteCount != 0 && Connected);
        }
        catch (Exception e)
        {
            Trace.WriteLine("Incoming errored.");
            Trace.WriteLine(e);
        }
        finally
        {
            Trace.WriteLine("Incoming closed.");
            SaveStatus();
            if (Connected)
                OnConnectionErrored();
        }
    }

    private async Task OutgoingLoopAsync()
    {
        try
        {
            int byteCount;
            var bytes = new byte[8192];

            do
            {
                byteCount = await Outgoing.ReadAsync(bytes, 0, bytes.Length);
                var content = Encoding.UTF8.GetString(bytes, 0, byteCount);

                // Insert fake player into roster
                const string roster = "<query xmlns='jabber:iq:riotgames:roster'>";
                if (!InsertedFakePlayer && content.Contains(roster))
                {
                    InsertedFakePlayer = true;
                    Trace.WriteLine("<!--SERVER TO RC ORIGINAL-->" + content);
                    content = content.Insert(content.IndexOf(roster, StringComparison.Ordinal) + roster.Length,
                        "<item jid='41c322a1-b328-495b-a004-5ccd3e45eae8@eu1.pvp.net' name='&#9;ROS Active!' subscription='both' puuid='41c322a1-b328-495b-a004-5ccd3e45eae8'>" +
                        "<group priority='9999'>ROS</group>" +
                        "<state>online</state>" +
                        "<id name='ROS Active!' tagline='...'/>" +
                        "<lol name='&#9;ROS Active!'/>" +
                        "<platforms><riot name='ROS Active' tagline='...'/></platforms>" +
                        "</item>");
                    var contentBytes = Encoding.UTF8.GetBytes(content);
                    await Incoming.WriteAsync(contentBytes, 0, contentBytes.Length);
                    Trace.WriteLine("<!--ROS TO RC-->" + content);
                }
                else
                {
                    await Incoming.WriteAsync(bytes, 0, byteCount);
                    Trace.WriteLine("<!--SERVER TO RC-->" + content);
                }
            } while (byteCount != 0 && Connected);
        }
        catch (Exception e)
        {
            Trace.WriteLine("Outgoing errored.");
            Trace.WriteLine(e);
        }
        finally
        {
            Trace.WriteLine("Outgoing closed.");
            SaveStatus();
            if (Connected)
                OnConnectionErrored();
        }
    }

    private async Task PossiblyRewriteAndResendPresenceAsync(string content, string targetStatus)
    {
        try
        {
            LastPresence = content;
            var wrappedContent = "<xml>" + content + "</xml>";
            var xml = XDocument.Load(new StringReader(wrappedContent));

            if (xml.Root is null)
                return;
            if (xml.Root.HasElements is false)
                return;

            foreach (var presence in xml.Root.Elements())
            {
                if (presence.Name != "presence")
                    continue;
                if (presence.Attribute("to") is not null)
                {
                    if (ConnectToMuc)
                        continue;
                    presence.Remove();
                }

                if (targetStatus != "chat" || presence.Element("games")?.Element("league_of_legends")?.Element("st")?.Value != "dnd")
                {
                    presence.Element("show")?.ReplaceNodes(targetStatus);
                    presence.Element("games")?.Element("league_of_legends")?.Element("st")?.ReplaceNodes(targetStatus);
                }

                if (targetStatus == "chat")
                    continue;
                presence.Element("status")?.Remove();

                if (targetStatus == "mobile")
                {
                    presence.Element("games")?.Element("league_of_legends")?.Element("p")?.Remove();
                    presence.Element("games")?.Element("league_of_legends")?.Element("m")?.Remove();
                }
                else
                {
                    presence.Element("games")?.Element("league_of_legends")?.Remove();
                }

                // Remove Legends of Runeterra presence
                presence.Element("games")?.Element("bacon")?.Remove();

                // Extracts current VALORANT from the user's own presence, so that we can show a fake
                // player with the proper version and avoid "Version Mismatch" from being shown.
                //
                // This isn't technically necessary, but people keep coming in and asking whether
                // the scary red text means ROS doesn't work, so might as well do this and
                // get a slightly better user experience.
                if (ValorantVersion is null)
                {
                    var valorantBase64 = presence.Element("games")?.Element("valorant")?.Element("p")?.Value;
                    if (valorantBase64 is not null)
                    {
                        var valorantPresence = Encoding.UTF8.GetString(Convert.FromBase64String(valorantBase64));
                        var valorantJson = JsonSerializer.Deserialize<JsonNode>(valorantPresence);
                        ValorantVersion = valorantJson?["partyClientVersion"]?.GetValue<string>();
                        Trace.WriteLine("Found VALORANT version: " + ValorantVersion);
                        // only resend
                        if (InsertedFakePlayer && ValorantVersion is not null)
                            await SendFakePlayerPresenceAsync();
                    }
                }

                // Remove VALORANT presence
                presence.Element("games")?.Element("valorant")?.Remove();
            }

            var sb = new StringBuilder();
            var xws = new XmlWriterSettings { OmitXmlDeclaration = true, Encoding = Encoding.UTF8, ConformanceLevel = ConformanceLevel.Fragment, Async = true };
            using (var xw = XmlWriter.Create(sb, xws))
            {
                foreach (var xElement in xml.Root.Elements())
                    xElement.WriteTo(xw);
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            await Outgoing.WriteAsync(bytes, 0, bytes.Length);
            Trace.WriteLine("<!--ROS TO SERVER-->" + sb);
        }
        catch (Exception e)
        {
            Trace.WriteLine(e);
            Trace.WriteLine("Error rewriting presence.");
        }
    }

    private async Task SendFakePlayerPresenceAsync()
    {
        SentFakePlayerPresence = true;
        // VALORANT requires a recent version to not display "Version Mismatch"
        var valorantPresence = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{{\"isValid\":true,\"partyId\":\"00000000-0000-0000-0000-000000000000\",\"partyClientVersion\":\"{ValorantVersion ?? "unknown"}\",\"accountLevel\":1000}}")
        );

        var randomStanzaId = Guid.NewGuid();
        var unixTimeMilliseconds = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        var presenceMessage =
            $"<presence from='41c322a1-b328-495b-a004-5ccd3e45eae8@eu1.pvp.net/RC-ROS' id='b-{randomStanzaId}'>" +
            "<games>" +
            $"<keystone><st>chat</st><s.t>{unixTimeMilliseconds}</s.t><s.p>keystone</s.p></keystone>" +
            $"<league_of_legends><st>chat</st><s.t>{unixTimeMilliseconds}</s.t><s.p>league_of_legends</s.p><p>{{&quot;pty&quot;:true}}</p></league_of_legends>" + // No Region s.r keeps it in the main "League" category rather than "Other Servers" in every region with "Group Games & Servers" active
            $"<valorant><st>chat</st><s.t>{unixTimeMilliseconds}</s.t><s.p>valorant</s.p><p>{valorantPresence}</p></valorant>" +
            $"<bacon><st>chat</st><s.t>{unixTimeMilliseconds}</s.t><s.l>bacon_availability_online</s.l><s.p>bacon</s.p></bacon>" +
            "</games>" +
            "<show>chat</show>" +
            "<platform>riot</platform>" +
            "</presence>";

        var bytes = Encoding.UTF8.GetBytes(presenceMessage);
        await Incoming.WriteAsync(bytes, 0, bytes.Length);
        Trace.WriteLine("<!--ROS TO RC-->" + presenceMessage);
    }

    private async Task SendIntroductionTextAsync()
    {
        if (!InsertedFakePlayer)
            return;
        SentIntroductionText = true;
        await SendMessageFromFakePlayerAsync("ようこそ ROSは実行中であり、あなたは現在表示されている " + Status +
                                             ". ゲームクライアントの表示とは裏腹に、ROSを手動で無効にしない限り、あなたはフレンドにオフラインで表示される。");
        await Task.Delay(200);
        await SendMessageFromFakePlayerAsync(
            "オフラインの状態で招待したい場合は、ROSを無効にして招待する必要があります。相手がロビーに来たら、またROSを有効にしてください。");
        await Task.Delay(200);
        await SendMessageFromFakePlayerAsync("ROSを有効または無効にしたり、その他の設定を行うには、トレイアイコンからROSを見つけてください。");
        await Task.Delay(200);
        await SendMessageFromFakePlayerAsync("楽しんでくれ！");
    }

    private async Task SendMessageFromFakePlayerAsync(string message)
    {
        var stamp = DateTime.UtcNow.AddSeconds(1).ToString("yyyy-MM-dd HH:mm:ss.fff");

        var chatMessage =
            $"<message from='41c322a1-b328-495b-a004-5ccd3e45eae8@eu1.pvp.net/RC-ROS' stamp='{stamp}' id='fake-{stamp}' type='chat'><body>{message}</body></message>";

        var bytes = Encoding.UTF8.GetBytes(chatMessage);
        await Incoming.WriteAsync(bytes, 0, bytes.Length);
        Trace.WriteLine("<!--ROS TO RC-->" + chatMessage);
    }

    private async Task UpdateStatusAsync(string newStatus)
    {
        if (string.IsNullOrEmpty(LastPresence))
            return;

        await PossiblyRewriteAndResendPresenceAsync(LastPresence, newStatus);

        if (newStatus == "chat")
            await SendMessageFromFakePlayerAsync("あなたは今、オンラインに姿を現している。");
        else
            await SendMessageFromFakePlayerAsync("あなたは今、こう見えている " + newStatus + ".");
    }

    private void LoadStatus()
    {
        if (File.Exists(StatusFile))
            Status = File.ReadAllText(StatusFile) == "mobile" ? "mobile" : "offline";
        else
            Status = "offline";
    }

    private void SaveStatus() => File.WriteAllText(StatusFile, Status);

    private void OnConnectionErrored()
    {
        Connected = false;
        ConnectionErrored?.Invoke(this, EventArgs.Empty);
    }
}
