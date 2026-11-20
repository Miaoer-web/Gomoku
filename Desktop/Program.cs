using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new Game());
    }
}

sealed class Game : Form
{
    static readonly Color WindowColor = Color.FromArgb(23, 26, 32);
    static readonly Color PanelColor = Color.FromArgb(30, 34, 42);
    static readonly Color InputColor = Color.FromArgb(42, 48, 59);
    static readonly Color TextColor = Color.FromArgb(236, 240, 247);
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
    readonly TextBox address = new() { Text = "127.0.0.1" };
    readonly TextBox code = new() { MaxLength = 4, PlaceholderText = "四位房间号" };
    readonly Label status = new()
    {
        Dock = DockStyle.Top, Height = 58, Padding = new Padding(8),
        TextAlign = ContentAlignment.MiddleCenter
    };
    readonly Button create = new SoftButton { Text = "创建" };
    readonly Button join = new SoftButton { Text = "加入" };
    readonly Button restart = new SoftButton { Text = "重新开局" };
    readonly Button leave = new SoftButton { Text = "退出" };
    readonly Button start = new SoftButton { Text = "开始" };
    readonly Button resign = new SoftButton { Text = "认输" };
    readonly SkinPicker skins = new();
    readonly Button skinImage = new SoftButton { Text = "选择图片" };
    readonly Button resetStone = new SoftButton { Text = "重置" };
    readonly Button[] seats = [new SoftButton(), new SoftButton(), new SoftButton()];
    readonly Button rotate = new SoftButton { Text = "轮换模式" };
    readonly Button manual = new SoftButton { Text = "互换模式" };
    readonly Button swap = new SoftButton { Text = "互换黑白" };
    readonly Label role = new() { TextAlign = ContentAlignment.MiddleCenter };
    readonly Label turn = new() { TextAlign = ContentAlignment.MiddleCenter };
    readonly Label notice = new() { Dock = DockStyle.Top, Height = 48, Visible = false };
    readonly Label inviteHint = new() { Dock = DockStyle.Top, Height = 25, Visible = false };
    readonly Board board = new() { Dock = DockStyle.Fill };
    readonly SoftPanel resultCard = new() { Visible = false, Tag = "section" };
    readonly ResultTitle resultTitle = new();
    readonly PictureBox resultPaw = new() { SizeMode = PictureBoxSizeMode.Zoom };
    readonly Button closeResult = new SoftButton { Text = "关闭" };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 650 };
    Uri? server;
    Ticket? ticket;
    State? state;
    bool busy;
    bool polling;
    DateTime? disconnectedSince;
    int actionVersion;
    string preferredSkin = Board.Presets[0];
    bool clayTheme;
    int? resultRound;
    bool lostResult;
    Process? roomService;
    string? hostDisplayIp;

    public Game()
    {
        Text = "爪爪落子";
        using var iconStream = typeof(Game).Assembly.GetManifestResourceStream("Paw.ico")!;
        using var paw = new Icon(iconStream);
        Icon = (Icon)paw.Clone();
        var screenHeight = Screen.PrimaryScreen?.WorkingArea.Height ?? 850;
        ClientSize = new Size(1180, Math.Clamp(screenHeight - 48, 510, 820));
        MinimumSize = new Size(820, 510);
        Font = new Font("Microsoft YaHei UI", 11);
        status.Font = new Font(Font.FontFamily, 15, FontStyle.Bold);
        BackColor = WindowColor;
        ForeColor = TextColor;
        status.ForeColor = TextColor;
        foreach (var input in new[] { address, code })
        {
            input.BackColor = InputColor;
            input.ForeColor = TextColor;
            input.BorderStyle = BorderStyle.FixedSingle;
        }
        var boardImage = new SoftButton { Text = "选择图片" };
        var resetBoard = new SoftButton { Text = "重置", Enabled = false };
        foreach (var button in new[] { create, join, restart, leave, start, resign, skinImage, resetStone, boardImage, resetBoard })
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(79, 90, 108);
            button.BackColor = InputColor;
            button.ForeColor = TextColor;
        }
        var accent = Color.FromArgb(48, 112, 105);
        create.BackColor = start.BackColor = accent;
        var themes = new SkinPicker();
        for (var index = 0; index < Board.Themes.Length; index++)
        {
            var theme = Board.Themes[index];
            themes.Add(theme.Name, Board.MakeBoardPreview(theme), index);
        }
        themes.Chosen += index =>
        {
            themes.SelectedIndex = index;
            board.SetTheme(Board.Themes[index]);
            resetBoard.Enabled = index != 0;
        };
        themes.SelectedIndex = 0;
        string[] skinNames = ["经典", "小猫", "小狗", "橘子", "苹果", "汉堡", "吉伊", "小八", "乌萨奇"];
        for (var index = 0; index < skinNames.Length; index++)
            skins.Add(skinNames[index], Board.MakePiecePreview(Board.Presets[index]), index);
        skins.SelectedIndex = 0;
        var sidebar = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var dark = new SoftButton { Text = "深色" };
        var clay = new SoftButton { Text = "浅色" };
        sidebar.Controls.Add(Section("软件主题", Row([50, 50], dark, clay)));
        var appearance = Section("外观",
            Row([17, 34, 29, 20], Caption("棋子"), skins, skinImage, resetStone),
            Row([17, 34, 29, 20], Caption("棋盘"), themes, boardImage, resetBoard));
        sidebar.Controls.Add(appearance);
        var turnRow = Row([100], turn);
        turnRow.Height = 26;
        sidebar.Controls.Add(Section("对局", turnRow,
            Row([50, 50], rotate, manual), Row([100], swap), Row([33, 33, 34], start, resign, restart)));
        var seatRow = Row([33, 33, 34], seats);
        void FitSeats() => seatRow.Height = Math.Max(seatRow.LogicalToDeviceUnits(54),
            SoftButton.MeasureLines(seats[0].Font, "已入座") * 2 + seatRow.LogicalToDeviceUnits(22));
        seatRow.HandleCreated += (_, _) => FitSeats();
        seatRow.DpiChangedAfterParent += (_, _) => FitSeats();
        seats[0].FontChanged += (_, _) => FitSeats();
        FitSeats();
        sidebar.Controls.Add(Section("席位", Row([100], role), seatRow));
        sidebar.Controls.Add(Section("房间",
            Row([28, 72], Caption("主机 IP"), address), Row([28, 72], Caption("房间号"), code),
            Row([33, 33, 34], create, join, leave), inviteHint, notice));

        var playArea = new SoftPanel
        {
            Dock = DockStyle.Fill, Margin = new Padding(0, 0, 16, 0),
            Padding = new Padding(12), Tag = "section"
        };
        playArea.Controls.Add(board);
        playArea.Controls.Add(status);
        resultPaw.Image = Icon!.ToBitmap();
        resultCard.Controls.AddRange([resultPaw, resultTitle, closeResult]);
        board.Controls.Add(resultCard);
        board.Resize += (_, _) => LayoutResultCard();
        closeResult.Click += (_, _) => resultCard.Hide();
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 1
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 392));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(playArea, 0, 0);
        layout.Controls.Add(sidebar, 1, 0);
        Controls.Add(layout);
        dark.Click += (_, _) => ChangeTheme(false, dark, clay);
        clay.Click += (_, _) => ChangeTheme(true, dark, clay);
        ChangeTheme(false, dark, clay);
        boardImage.Click += (_, _) =>
        {
            try
            {
                using var selected = PickImage();
                if (selected != null)
                {
                    board.SetSurface(ScaleImage(selected, 900, false));
                    resetBoard.Enabled = true;
                }
            }
            catch (Exception ex) { ShowNotice("无法打开棋盘图片。 " + ex.Message); }
        };
        resetBoard.Click += (_, _) =>
        {
            themes.SelectedIndex = 0;
            board.SetTheme(Board.Themes[0]);
            resetBoard.Enabled = false;
        };
        skins.Chosen += async index => await Run(() => SetStone(Board.Presets[index]));
        skinImage.Click += async (_, _) => await Run(PickStone);
        resetStone.Click += async (_, _) => await Run(() => SetStone(Board.Presets[0]));
        create.Click += async (_, _) => await Run(() => Connect(true));
        join.Click += async (_, _) => await Run(() => Connect(false));
        leave.Click += async (_, _) => await Run(LeaveRoom);
        for (var index = 0; index < seats.Length; index++)
        {
            int side = index + 1;
            seats[index].Click += async (_, _) => await Run(() => RoomAction("seat", new { side }));
        }
        rotate.Click += async (_, _) => await Run(() => RoomAction("mode", new { value = "rotate" }));
        manual.Click += async (_, _) => await Run(() => RoomAction("mode", new { value = "swap" }));
        swap.Click += async (_, _) => await Run(() => RoundAction("swap"));
        start.Click += async (_, _) => await Run(() => RoundAction("start"));
        resign.Click += async (_, _) => await Run(() => RoundAction("resign"));
        restart.Click += async (_, _) => await Run(() => RoundAction("restart"));
        board.Picked += async (x, y) =>
        {
            if (state is { Ready: true, Started: true, Result: 0 } current && current.Turn == current.Side &&
                current.Board[y * 15 + x] == 0)
                await Run(() => RoomAction("move", new { x, y, current.Round, current.MoveCount, current.Side }));
        };
        timer.Tick += async (_, _) => await Poll();
        timer.Start();
        ShowState();
    }

    void LayoutResultCard()
    {
        var width = Math.Min(Math.Max(1, board.Width - 16), board.LogicalToDeviceUnits(350));
        var height = Math.Min(Math.Max(1, board.Height - 16), board.LogicalToDeviceUnits(188));
        resultCard.SetBounds((board.Width - width) / 2, (board.Height - height) / 2, width, height);
        var iconSize = Math.Min(height / 4, board.LogicalToDeviceUnits(44));
        resultPaw.SetBounds((width - iconSize) / 2, height / 10, iconSize, iconSize);
        resultTitle.SetBounds(0, height / 3, width, height / 3);
        var buttonWidth = width / 3;
        closeResult.SetBounds((width - buttonWidth) / 2, height * 7 / 10, buttonWidth, height / 5);
    }

    void RefreshResult()
    {
        if (state is not { Result: > 0 } current)
        {
            resultRound = null;
            resultCard.Hide();
            return;
        }
        if (resultRound == current.Round) return;
        resultRound = current.Round;
        lostResult = current.Outcome == "loss";
        resultTitle.Text = current.Result == 3 ? "棋盘已满，请重新开局" :
            current.Outcome == "watch" ? $"{SeatName(current.Result)}赢啦~" : lostResult ? "你输啦~" : "你赢啦~";
        resultTitle.Invalidate();
        ColorResultTitle();
        LayoutResultCard();
        resultCard.Show();
        resultCard.BringToFront();
    }

    void ColorResultTitle()
    {
        resultTitle.ForeColor = lostResult
            ? (clayTheme ? Color.FromArgb(65, 111, 151) : Color.FromArgb(157, 201, 239))
            : (clayTheme ? Color.FromArgb(153, 77, 56) : Color.FromArgb(246, 177, 148));
    }

    void ChangeTheme(bool useClay, Button dark, Button clay)
    {
        clayTheme = useClay;
        var window = useClay ? Color.FromArgb(246, 241, 236) : Color.FromArgb(33, 35, 41);
        var panel = useClay ? Color.FromArgb(250, 246, 241) : Color.FromArgb(45, 48, 55);
        var input = useClay ? Color.FromArgb(242, 234, 224) : Color.FromArgb(57, 60, 69);
        var text = useClay ? Color.FromArgb(62, 49, 43) : Color.FromArgb(249, 237, 220);
        var accent = useClay ? Color.FromArgb(156, 180, 132) : Color.FromArgb(73, 76, 85);
        var heading = useClay ? Color.FromArgb(246, 207, 188) : Color.FromArgb(235, 169, 146);
        var border = useClay ? Color.FromArgb(222, 210, 198) : Color.FromArgb(80, 83, 91);
        BackColor = window;
        ForeColor = text;
        Apply(this);
        ColorResultTitle();
        foreach (var option in new[] { dark, clay })
        {
            bool active = option == (useClay ? clay : dark);
            option.BackColor = active ? accent : input;
            option.FlatAppearance.BorderColor = active ? Color.FromArgb(145, 164, 131) : border;
        }
        if (IsHandleCreated) ApplyTitleTheme();
        RefreshSeats();

        void Apply(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                if (control == board) { Apply(control); continue; }
                control.ForeColor = text;
                if (control is Button button)
                {
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderColor = border;
                    button.BackColor = button == start || button == create ? accent : input;
                }
                else if (control is TextBox) control.BackColor = input;
                else if (Equals(control.Tag, "section"))
                {
                    control.BackColor = panel;
                    if (control is SoftPanel card) card.Outline = border;
                }
                else if (Equals(control.Tag, "heading"))
                {
                    control.BackColor = Color.Transparent;
                    control.ForeColor = useClay ? Color.FromArgb(139, 75, 56) : heading;
                }
                else if (control == status)
                {
                    control.BackColor = panel;
                }
                Apply(control);
            }
        }
    }

    static Label Caption(string text) => new()
    {
        Text = text, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(3, 4, 0, 7),
        TextAlign = ContentAlignment.MiddleLeft
    };

    static TableLayoutPanel Row(float[] widths, params Control[] controls)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 38, ColumnCount = controls.Length, RowCount = 1,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize
        };
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (var i = 0; i < controls.Length; i++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, widths[i]));
            var control = controls[i];
            control.Margin = new Padding(3, 4, 3, 4);
            control.Dock = control is Button or Label ? DockStyle.Fill : DockStyle.None;
            if (control.Dock == DockStyle.None) control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            row.Controls.Add(control, i, 0);
        }
        return row;
    }

    static Panel Section(string title, params Control[] rows)
    {
        var section = new SoftPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 8, 14, 8), BackColor = PanelColor, Tag = "section"
        };
        for (int i = rows.Length - 1; i >= 0; i--) section.Controls.Add(rows[i]);
        var heading = Caption(title);
        heading.Tag = "heading";
        heading.Font = new Font("Microsoft YaHei UI", 14, FontStyle.Bold);
        heading.ForeColor = Color.FromArgb(130, 221, 209);
        section.Controls.Add(heading);
        var container = new Panel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 0, 0, 9)
        };
        container.Controls.Add(section);
        return container;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTitleTheme();
    }

    void ApplyTitleTheme()
    {
        int dark = clayTheme ? 0 : 1;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    string RoomPath => "/rooms/" + ticket!.Code;

    Image? PickImage()
    {
        using var dialog = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
        return dialog.ShowDialog(this) == DialogResult.OK ? Image.FromFile(dialog.FileName) : null;
    }

    static Bitmap ScaleImage(Image source, int size, bool square)
    {
        if (square)
        {
            var side = Math.Min(source.Width, source.Height);
            var cropped = new Bitmap(size, size);
            using var canvas = Graphics.FromImage(cropped);
            canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
            canvas.DrawImage(source, new Rectangle(0, 0, size, size),
                (source.Width - side) / 2, (source.Height - side) / 2, side, side, GraphicsUnit.Pixel);
            return cropped;
        }
        var scale = (float)size / Math.Max(source.Width, source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var result = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(result);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, (result.Width - width) / 2, (result.Height - height) / 2, width, height);
        return result;
    }

    async Task PickStone()
    {
        using var selected = PickImage();
        if (selected == null) return;
        using var image = ScaleImage(selected, 80, true);
        using var bytes = new MemoryStream();
        image.Save(bytes, ImageFormat.Png);
        await SetStone("png:" + Convert.ToBase64String(bytes.ToArray()));
    }

    async Task SetStone(string value)
    {
        if (ticket != null)
            state = await Send<State>(HttpMethod.Post, RoomPath + "/skin", new { value });
        preferredSkin = value;
        ShowState();
    }

    async Task RoundAction(string action)
    {
        if (state is { } current)
            await RoomAction(action, new { current.Round, current.Side });
    }

    async Task RoomAction(string action, object body) =>
        UpdateState(await Send<State>(HttpMethod.Post, RoomPath + "/" + action, body));

    async Task Connect(bool hosting)
    {
        if (hosting)
        {
            await CreateHostedRoom();
            return;
        }
        var host = address.Text.Trim();
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
            host = new UriBuilder("http", ip.ToString(), 5080).Uri.AbsoluteUri;
        if (!Uri.TryCreate(host, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new InvalidOperationException("请输入主机 IP，例如 192.168.1.20。");
        var roomCode = code.Text.Trim();
        if (roomCode.Length != 4 || roomCode.Any(c => c < '0' || c > '9'))
            throw new InvalidOperationException("请输入四位数字房间号。");
        server = uri;
        await EnterRoom("/rooms/" + roomCode + "/join");
    }

    async Task CreateHostedRoom()
    {
        server = new Uri("http://127.0.0.1:5080");
        await StartRoomService();
        try
        {
            hostDisplayIp = FindLanIp();
            await EnterRoom("/rooms");
            address.Text = hostDisplayIp ?? "127.0.0.1";
            ShowState();
        }
        catch
        {
            if (!IsDisposed) ClearRoom();
            throw;
        }
    }

    async Task EnterRoom(string path)
    {
        ticket = await Send<Ticket>(HttpMethod.Post, path);
        if (ticket.Code.Length != 4)
        {
            ticket = null;
            throw new InvalidOperationException("房间服务版本过旧，请让房主使用新版游戏重新建房。");
        }
        code.Text = ticket.Code;
        ShowState();
        UpdateState(await Send<State>(HttpMethod.Get, RoomPath));
        if (preferredSkin != Board.Presets[0]) await SetStone(preferredSkin);
    }

    async Task StartRoomService()
    {
        var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Server", "Server.exe"));
        if (!File.Exists(executable))
            throw new InvalidOperationException("缺少房间服务文件。请解压整个压缩包后再打开游戏。");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        info.ArgumentList.Add("--urls");
        info.ArgumentList.Add("http://0.0.0.0:5080");
        info.Environment["GOMOKU_HOST_PROCESS_ID"] = Environment.ProcessId.ToString();
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, output) =>
        {
            if (output.Data == "GOMOKU_ROOM_READY") ready.TrySetResult(true);
        };
        process.ErrorDataReceived += (_, _) => { };
        process.Exited += (_, _) => ready.TrySetResult(false);
        roomService = process;
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!await ready.Task.WaitAsync(TimeSpan.FromSeconds(8)))
                throw new InvalidOperationException("房间服务启动失败。请关闭旧版房间服务，并确认已安装 ASP.NET Core 10 运行环境。");
        }
        catch (TimeoutException)
        {
            StopRoomService();
            throw new InvalidOperationException("房间服务启动超时，请重新创建。");
        }
        catch
        {
            StopRoomService();
            throw;
        }
    }

    void StopRoomService()
    {
        var process = roomService;
        roomService = null;
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(500);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }

    static string? FindLanIp() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
            adapter.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
        .Select(adapter => new { Adapter = adapter, Properties = adapter.GetIPProperties() })
        .OrderByDescending(entry => entry.Adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
        .ThenByDescending(entry => entry.Properties.GatewayAddresses.Any(gateway =>
            gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)))
        .SelectMany(entry => entry.Properties.UnicastAddresses)
        .Select(entry => entry.Address)
        .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork &&
            !IPAddress.IsLoopback(ip) && !ip.ToString().StartsWith("169.254."))?.ToString();

    async Task<T> Send<T>(HttpMethod method, string path, object? body = null)
    {
        using var response = await Request(method, path, body);
        return await response.Content.ReadFromJsonAsync<T>() ?? throw new InvalidOperationException("房间服务返回空数据。");
    }

    async Task<HttpResponseMessage> Request(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(server!, path));
        if (ticket != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ticket.Token);
        if (body != null) request.Content = JsonContent.Create(body);
        var response = await http.SendAsync(request);
        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            var error = await response.Content.ReadFromJsonAsync<Failure>();
            throw new RoomError(error?.Message ?? "房间服务请求失败。", (int)response.StatusCode);
        }
    }

    async Task LeaveRoom()
    {
        if (roomService != null)
        {
            ClearRoom();
            return;
        }
        try { using var response = await Request(HttpMethod.Post, RoomPath + "/leave"); }
        catch (RoomError ex) when (ex.Status is 401 or 404) { }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        ClearRoom();
    }

    void ClearRoom()
    {
        if (roomService != null)
        {
            StopRoomService();
            address.Text = "127.0.0.1";
        }
        hostDisplayIp = null;
        ticket = null;
        state = null;
        disconnectedSince = null;
        ShowState();
    }

    async Task Poll()
    {
        if (ticket == null || polling || busy) return;
        polling = true;
        var version = actionVersion;
        try
        {
            var next = await Send<State>(HttpMethod.Get, RoomPath);
            if (IsDisposed || version != actionVersion || busy) return;
            if (disconnectedSince != null) ShowNotice("");
            disconnectedSince = null;
            UpdateState(next);
        }
        catch (Exception ex)
        {
            if (IsDisposed || version != actionVersion || busy) return;
            if (ex is RoomError { Status: 401 or 404 }) ClearRoom();
            else
            {
                disconnectedSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - disconnectedSince >= TimeSpan.FromSeconds(12))
                {
                    ClearRoom();
                    ShowNotice("与房间服务断开，已退出房间。");
                    return;
                }
            }
            ShowNotice(ConnectionMessage(ex));
        }
        finally { polling = false; }
    }

    async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        actionVersion++;
        ShowNotice("");
        skins.Enabled = skinImage.Enabled = resetStone.Enabled = false;
        try { await action(); }
        catch (Exception ex)
        {
            if (!IsDisposed) ShowNotice(ConnectionMessage(ex));
        }
        finally
        {
            busy = false;
            if (!IsDisposed) { leave.Enabled = ticket != null; UpdateSkinControls(); }
        }
    }

    string ConnectionMessage(Exception ex) => ex is HttpRequestException or TaskCanceledException
        ? (server?.IsLoopback == true ? "连不上本机房间服务，请让房主先点「创建」。"
            : "连不上主机，请检查房间服务、局域网连接和主机 IP。")
        : ex.Message;

    void ShowNotice(string text)
    {
        notice.Text = text;
        notice.Visible = text.Length > 0;
    }

    void UpdateState(State value)
    {
        var previous = state;
        if (previous != null && value.SameAs(previous)) return;
        state = value;
        ShowState();
    }

    void ShowState()
    {
        var cells = state?.Board;
        var redraw = board.Preview != (ticket == null) || board.LastMove != state?.LastMove ||
            (cells == null ? board.Cells != null : board.Cells == null || !cells.SequenceEqual(board.Cells));
        board.Cells = cells;
        board.Preview = ticket == null;
        board.LastMove = state?.LastMove;
        if (board.SetStones(ticket == null ? [preferredSkin, Board.Presets[0]] : state?.Skins) || redraw)
            board.Invalidate();
        address.ReadOnly = code.ReadOnly = ticket != null;
        create.Enabled = join.Enabled = ticket == null;
        inviteHint.Visible = roomService != null && ticket != null;
        inviteHint.Text = hostDisplayIp == null ? "未检测到局域网 IP，可在本机试玩。" : "把上面的 IP 和四位房间号发给好友。";
        leave.Enabled = ticket != null && !busy;
        UpdateSkinControls();
        var playing = state is { Side: 1 or 2 };
        var running = state is { Started: true, Result: 0 };
        start.Enabled = state is { Host: true, Ready: true, Started: false };
        resign.Enabled = playing && running && state!.Ready;
        restart.Enabled = playing && state is { Ready: true, Started: true };
        rotate.Enabled = manual.Enabled = playing && !running;
        swap.Enabled = playing && !running && state is { Ready: true, Mode: "swap" };
        swap.Parent!.Visible = state is { Mode: "swap" };
        turn.Text = running && state!.Ready ? $"轮到{SeatName(state.Turn)}" : "";
        var occupied = state?.Seats ?? [false, false, false];
        int players = occupied.Take(2).Count(used => used);
        int watching = occupied[2] ? 1 : 0;
        status.Text = $"房间 {players + watching} 人 · 对局 {players} · 观战 {watching}";
        RefreshSeats();
        RefreshResult();
    }

    static string SeatName(int side) => side switch { 1 => "黑棋", 2 => "白棋", _ => "观战" };

    void RefreshSeats()
    {
        role.Text = state?.Side switch { 1 => "你在对局 · 执黑", 2 => "你在对局 · 执白", 3 => "你在观战", _ => "尚未入座" };
        role.BackColor = state == null ? Color.Transparent :
            clayTheme ? Color.FromArgb(246, 207, 188) : Color.FromArgb(74, 55, 51);
        var active = Color.FromArgb(145, 164, 131);
        var border = clayTheme ? Color.FromArgb(206, 190, 175) : Color.FromArgb(80, 83, 91);
        for (var index = 0; index < seats.Length; index++)
        {
            var button = seats[index];
            var own = state?.Side == index + 1;
            var occupied = state?.Seats[index] == true;
            button.Text = SeatName(index + 1) + "\n" + (own ? "你" : occupied ? "已入座" : "空席");
            button.Enabled = state != null && !occupied;
            button.FlatAppearance.BorderColor = own ? active : border;
            button.Invalidate();
        }
        rotate.FlatAppearance.BorderColor = state?.Mode != "swap" ? active : border;
        manual.FlatAppearance.BorderColor = state?.Mode == "swap" ? active : border;
        rotate.Invalidate();
        manual.Invalidate();
    }

    void UpdateSkinControls()
    {
        skins.Enabled = skinImage.Enabled = !busy;
        var index = state is { Side: 1 or 2, Skins.Length: 2 } current
            ? Array.IndexOf(Board.Presets, current.Skins[current.Side - 1])
            : Array.IndexOf(Board.Presets, preferredSkin);
        skins.SelectedIndex = index;
        resetStone.Enabled = skins.Enabled && index != 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { StopRoomService(); timer.Dispose(); http.Dispose(); resultPaw.Image?.Dispose(); }
        base.Dispose(disposing);
    }

    sealed record Ticket(string Code, string Token);
    sealed record State(int[] Board, int Turn, int Result, int Round, int MoveCount,
        bool Ready, bool Started, int ResignedBy, string[] Skins, int? LastMove,
        int Side, bool Host, string Mode, bool[] Seats, string Outcome)
    {
        public bool SameAs(State other) =>
            Turn == other.Turn && Result == other.Result && Round == other.Round && MoveCount == other.MoveCount &&
            Ready == other.Ready && Started == other.Started && ResignedBy == other.ResignedBy &&
            LastMove == other.LastMove && Side == other.Side && Host == other.Host && Mode == other.Mode &&
            Outcome == other.Outcome &&
            Seats.SequenceEqual(other.Seats) &&
            Skins.SequenceEqual(other.Skins) && Board.SequenceEqual(other.Board);
    }
    sealed record Failure(string Message);
    sealed class RoomError(string message, int status) : Exception(message) { public int Status { get; } = status; }
}

static class SoftShape
{
    public static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter < 2) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

sealed class ResultTitle : Control
{
    protected override void OnPaint(PaintEventArgs e)
    {
        var size = Text.Length > 8 ? 15 : 27;
        using var rounded = new Font("YouYuan", size, FontStyle.Bold);
        using var fallback = new Font("Microsoft YaHei UI", size, FontStyle.Bold);
        var font = rounded.Name == "YouYuan" || rounded.Name == "幼圆" ? rounded : fallback;
        const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(e.Graphics, Text, font, ClientRectangle, ForeColor, flags);
        if (size > 15)
        {
            var shifted = ClientRectangle;
            shifted.Offset(1, 0);
            TextRenderer.DrawText(e.Graphics, Text, font, shifted, ForeColor, flags);
        }
    }
}

sealed class SoftPanel : Panel
{
    internal Color Outline;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var card = SoftShape.Rounded(bounds, 14);
        using var fill = new LinearGradientBrush(bounds, ControlPaint.Light(BackColor, .06f),
            BackColor, LinearGradientMode.Vertical);
        using var edge = new Pen(Color.FromArgb(115, Outline));
        e.Graphics.FillPath(fill, card);
        e.Graphics.DrawPath(edge, card);
    }
}

class SoftButton : Button
{
    const TextFormatFlags Centered = TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter |
        TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
    bool hovered;

    internal static int MeasureLines(Font font, string line) =>
        TextRenderer.MeasureText(line, font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height;

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
        bounds.Height -= 2;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var parentColor = Parent?.BackColor ?? BackColor;
        var fillColor = !Enabled ? Color.FromArgb((BackColor.R + parentColor.R) / 2,
            (BackColor.G + parentColor.G) / 2, (BackColor.B + parentColor.B) / 2) :
            hovered ? ControlPaint.Light(BackColor, .14f) : BackColor;
        e.Graphics.Clear(parentColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var surface = SoftShape.Rounded(bounds, 9);
        var shadowBounds = bounds;
        shadowBounds.Offset(0, 2);
        using var shadow = SoftShape.Rounded(shadowBounds, 9);
        using var shade = new SolidBrush(Color.FromArgb(50, Color.Black));
        using var fill = new LinearGradientBrush(bounds, ControlPaint.Light(fillColor, .13f),
            fillColor, LinearGradientMode.Vertical);
        using var edge = new Pen(Focused ? ForeColor : FlatAppearance.BorderColor);
        e.Graphics.FillPath(shade, shadow);
        e.Graphics.FillPath(fill, surface);
        e.Graphics.DrawPath(edge, surface);
        var textColor = Enabled ? ForeColor : Color.FromArgb(
            (ForeColor.R + fillColor.R) / 2, (ForeColor.G + fillColor.G) / 2, (ForeColor.B + fillColor.B) / 2);
        var textBounds = bounds;
        if (Image is { } thumbnail)
        {
            var size = Math.Min(LogicalToDeviceUnits(21), bounds.Height - 6);
            if (size > 0)
            {
                e.Graphics.DrawImage(thumbnail, bounds.Left + 6, bounds.Top + (bounds.Height - size) / 2, size, size);
                textBounds.X += size + 8;
                textBounds.Width -= size + 8;
            }
        }
        var lines = Text.Split('\n');
        if (lines.Length == 1)
        {
            TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, textColor, Centered | TextFormatFlags.EndEllipsis);
            return;
        }
        int lineHeight = lines.Max(line => MeasureLines(Font, line));
        var lineBounds = new Rectangle(textBounds.X,
            textBounds.Y + (textBounds.Height - lineHeight * lines.Length) / 2, textBounds.Width, lineHeight);
        foreach (var line in lines)
        {
            TextRenderer.DrawText(e.Graphics, line, Font, lineBounds, textColor, Centered);
            lineBounds.Offset(0, lineHeight);
        }
    }
}

sealed class SkinPicker : SoftButton
{
    readonly ContextMenuStrip menu = new() { ShowImageMargin = true, ShowCheckMargin = false };
    readonly List<Image> thumbnails = [];
    int selectedIndex = -1;
    internal event Action<int>? Chosen;

    internal SkinPicker()
    {
        menu.Renderer = new PickerRenderer(this);
        menu.Opened += (_, _) => ShapeMenu();
        menu.SizeChanged += (_, _) => ShapeMenu();
        Click += (_, _) =>
        {
            menu.Font = Font;
            menu.BackColor = BackColor;
            menu.ForeColor = ForeColor;
            menu.ImageScalingSize = new Size(LogicalToDeviceUnits(24), LogicalToDeviceUnits(24));
            foreach (ToolStripMenuItem item in menu.Items) item.ForeColor = ForeColor;
            menu.Show(this, new Point(0, Height));
        };
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            selectedIndex = value;
            for (var index = 0; index < menu.Items.Count; index++)
                ((ToolStripMenuItem)menu.Items[index]).Checked = index == value;
            Image = value < 0 ? null : thumbnails[value];
            Text = (value < 0 ? "本地图片" : menu.Items[value].Text) + "  ▾";
        }
    }

    internal void Add(string label, Image thumbnail, int index)
    {
        thumbnails.Add(thumbnail);
        var item = new ToolStripMenuItem(label, thumbnail) { Padding = new Padding(6, 3, 12, 3) };
        item.Click += (_, _) => Chosen?.Invoke(index);
        menu.Items.Add(item);
    }

    void ShapeMenu()
    {
        if (menu.Width <= 0 || menu.Height <= 0) return;
        using var outline = SoftShape.Rounded(new Rectangle(Point.Empty, menu.Size), 9);
        var previous = menu.Region;
        menu.Region = new Region(outline);
        previous?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Image = null;
            menu.Dispose();
            foreach (var thumbnail in thumbnails) thumbnail.Dispose();
        }
        base.Dispose(disposing);
    }

    sealed class PickerRenderer(SkinPicker picker) : ToolStripSystemRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) =>
            e.Graphics.Clear(picker.BackColor);

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item is not ToolStripMenuItem item || (!item.Checked && !item.Selected)) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = SoftShape.Rounded(Rectangle.Inflate(new Rectangle(Point.Empty, item.Size), -2, -1), 7);
            var light = picker.BackColor.GetBrightness() > .5f;
            using var fill = new SolidBrush(light ? Color.FromArgb(242, 217, 203) : Color.FromArgb(99, 73, 68));
            e.Graphics.FillPath(fill, shape);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = SoftShape.Rounded(Rectangle.Inflate(e.ToolStrip.ClientRectangle, -1, -1), 9);
            using var border = new Pen(picker.FlatAppearance.BorderColor);
            e.Graphics.DrawPath(border, shape);
        }
    }
}

sealed class Board : Control
{
    static readonly int[] PreviewCells = CreatePreview();

    static int[] CreatePreview()
    {
        var cells = new int[15 * 15];
        cells[7 * 15 + 7] = 1;
        return cells;
    }

    public sealed record Theme(string Name, Color Surface, Color Grid);
    public static readonly string[] Presets = ["classic", "kitten", "puppy", "orange", "apple", "burger", "chiikawa", "hachiware", "usagi"];
    static readonly string[] ColorPresets = ["classic", "jade", "metal", "candy", "porcelain", "amber"];
    static readonly Dictionary<string, Image> Sprites = Presets.Skip(1).ToDictionary(name => name, name =>
    {
        using var source = typeof(Board).Assembly.GetManifestResourceStream($"Pieces.{name}.png")!;
        using var image = Image.FromStream(source);
        return (Image)new Bitmap(image);
    });

    internal static Image MakePiecePreview(string key)
    {
        if (Sprites.TryGetValue(key, out var sprite)) return new Bitmap(sprite, 32, 32);
        var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.FillEllipse(Brushes.Black, 2, 3, 19, 19);
        graphics.FillEllipse(Brushes.FloralWhite, 11, 10, 19, 19);
        graphics.DrawEllipse(Pens.Gray, 11, 10, 19, 19);
        return bitmap;
    }

    internal static Image MakeBoardPreview(Theme theme)
    {
        var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(theme.Surface);
        using var line = new Pen(theme.Grid);
        for (var position = 5; position <= 27; position += 7)
        {
            graphics.DrawLine(line, position, 5, position, 26);
            graphics.DrawLine(line, 5, position, 26, position);
        }
        return bitmap;
    }
    static readonly (Color Dark, Color Light)[] StoneColors =
    [
        (Color.Black, Color.FloralWhite),
        (Color.FromArgb(14, 78, 59), Color.FromArgb(189, 241, 208)),
        (Color.FromArgb(56, 62, 79), Color.FromArgb(233, 209, 142)),
        (Color.FromArgb(101, 52, 155), Color.FromArgb(255, 177, 204)),
        (Color.FromArgb(22, 58, 119), Color.FromArgb(225, 239, 255)),
        (Color.FromArgb(119, 52, 17), Color.FromArgb(255, 203, 103))
    ];
    readonly string[] stoneSkins = ["classic", "classic"];
    readonly Bitmap?[] stoneImages = new Bitmap?[2];
    Bitmap? surface;

    public static readonly Theme[] Themes =
    [
        new("石墨灰", Color.FromArgb(65, 72, 84), Color.FromArgb(177, 188, 204)),
        new("经典木色", Color.FromArgb(229, 183, 116), Color.FromArgb(95, 63, 37)),
        new("胡桃木", Color.FromArgb(111, 70, 50), Color.FromArgb(230, 180, 126)),
        new("深海蓝", Color.FromArgb(28, 70, 99), Color.FromArgb(134, 196, 221)),
        new("墨玉绿", Color.FromArgb(33, 83, 70), Color.FromArgb(151, 207, 175)),
        new("暮紫", Color.FromArgb(81, 57, 106), Color.FromArgb(197, 169, 222))
    ];
    Theme theme = Themes[0];

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int[]? Cells { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int? LastMove { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Preview { get; set; }
    public event Action<int, int>? Picked;
    float Step => Math.Max(1, (Math.Min(Width, Height) - 48) / 14f);
    PointF Origin => new((Width - 14 * Step) / 2, (Height - 14 * Step) / 2);

    public Board() { DoubleBuffered = true; ResizeRedraw = true; BackColor = theme.Surface; }

    public void SetTheme(Theme value)
    {
        theme = value;
        BackColor = value.Surface;
        SetSurface(null);
    }

    public void SetSurface(Bitmap? value)
    {
        surface?.Dispose();
        surface = value;
        Invalidate();
    }

    public bool SetStones(string[]? values)
    {
        bool changed = false;
        for (var side = 0; side < 2; side++)
        {
            var value = values is { Length: 2 } ? values[side] : "classic";
            if (stoneSkins[side] == value) continue;
            changed = true;
            stoneSkins[side] = value;
            stoneImages[side]?.Dispose();
            stoneImages[side] = null;
            if (!value.StartsWith("png:", StringComparison.Ordinal)) continue;
            try
            {
                var data = Convert.FromBase64String(value[4..]);
                ReadOnlySpan<byte> header = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13,
                    73, 72, 68, 82, 0, 0, 0, 80, 0, 0, 0, 80];
                if (!data.AsSpan().StartsWith(header)) continue;
                using var bytes = new MemoryStream(data);
                using var decoded = Image.FromStream(bytes);
                if (decoded.Width == 80 && decoded.Height == 80)
                    stoneImages[side] = new Bitmap(decoded);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or ExternalException) { }
        }
        return changed;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            surface?.Dispose();
            foreach (var stone in stoneImages) stone?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        if (surface != null && Width > 0 && Height > 0)
        {
            var scale = Math.Max(Width / (float)surface.Width, Height / (float)surface.Height);
            var width = surface.Width * scale;
            var height = surface.Height * scale;
            g.DrawImage(surface, (Width - width) / 2, (Height - height) / 2, width, height);
            using var tint = new SolidBrush(Color.FromArgb(125, 16, 20, 25));
            g.FillRectangle(tint, ClientRectangle);
        }
        var origin = Origin;
        var step = Step;
        var grid = surface == null ? theme.Grid : Color.WhiteSmoke;
        using var line = new Pen(grid);
        using var star = new SolidBrush(grid);
        using var blackEdge = new Pen(Color.FromArgb(148, 155, 168));
        using var whiteEdge = new Pen(Color.FromArgb(81, 85, 93));
        for (var i = 0; i < 15; i++)
        {
            g.DrawLine(line, origin.X + i * step, origin.Y, origin.X + i * step, origin.Y + 14 * step);
            g.DrawLine(line, origin.X, origin.Y + i * step, origin.X + 14 * step, origin.Y + i * step);
        }
        foreach (var (x, y) in new[] { (3, 3), (11, 3), (7, 7), (3, 11), (11, 11) })
            g.FillEllipse(star, origin.X + x * step - 3, origin.Y + y * step - 3, 6, 6);
        var cells = Preview ? PreviewCells : Cells;
        if (cells == null) return;
        var radius = step * .43f;
        var colors = stoneSkins.Select(value => StoneColors[Math.Max(0, Array.IndexOf(ColorPresets, value))]).ToArray();
        using var black = new SolidBrush(colors[0].Dark);
        using var white = new SolidBrush(colors[1].Light);
        for (var i = 0; i < cells.Length; i++)
        {
            if (cells[i] == 0) continue;
            var stone = new RectangleF(origin.X + i % 15 * step - radius,
                origin.Y + i / 15 * step - radius, radius * 2, radius * 2);
            var side = cells[i] - 1;
            if (Sprites.TryGetValue(stoneSkins[side], out var sprite))
            {
                g.DrawImage(sprite, stone);
                var badgeSize = Math.Max(5, step * .18f);
                var badge = new RectangleF(stone.Right - badgeSize, stone.Bottom - badgeSize, badgeSize, badgeSize);
                g.FillEllipse(side == 0 ? Brushes.Black : Brushes.White, badge);
                g.DrawEllipse(side == 0 ? Pens.White : Pens.Black, badge);
                continue;
            }
            g.FillEllipse(side == 0 ? black : white, stone);
            if (stoneImages[side] is { } picture)
            {
                using var circle = new GraphicsPath();
                circle.AddEllipse(stone);
                var saved = g.Save();
                g.SetClip(circle, CombineMode.Intersect);
                g.DrawImage(picture, stone);
                g.Restore(saved);
            }
            using var face = new GraphicsPath();
            face.AddEllipse(stone);
            using var lighting = new PathGradientBrush(face)
            {
                CenterPoint = new PointF(stone.X + radius * .68f, stone.Y + radius * .65f),
                CenterColor = Color.FromArgb(68, 255, 255, 255),
                SurroundColors = [Color.FromArgb(105, 0, 0, 0)]
            };
            g.FillPath(lighting, face);
            g.DrawEllipse(side == 0 ? blackEdge : whiteEdge, stone);
        }
        if (LastMove is int last && last >= 0 && last < cells.Length && cells[last] != 0)
        {
            var centerX = origin.X + last % 15 * step;
            var centerY = origin.Y + last / 15 * step;
            var ringRadius = step * .485f;
            using var outline = new Pen(Color.FromArgb(180, 41, 24, 14), 3.5f);
            using var marker = new Pen(Color.FromArgb(255, 155, 51), 1.8f);
            var ring = new RectangleF(centerX - ringRadius, centerY - ringRadius, ringRadius * 2, ringRadius * 2);
            g.DrawEllipse(outline, ring);
            g.DrawEllipse(marker, ring);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var origin = Origin;
        var fx = (e.X - origin.X) / Step;
        var fy = (e.Y - origin.Y) / Step;
        var x = (int)Math.Round(fx);
        var y = (int)Math.Round(fy);
        if (x is >= 0 and < 15 && y is >= 0 and < 15 &&
            Math.Abs(fx - x) <= .45 && Math.Abs(fy - y) <= .45)
            Picked?.Invoke(x, y);
    }
}
