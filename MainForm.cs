using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace InternetPriority
{
    internal sealed class MainForm : Form
    {
        private static readonly Color Page = Color.FromArgb(243, 247, 252);
        private static readonly Color Ink = Color.FromArgb(20, 35, 59);
        private static readonly Color Secondary = Color.FromArgb(71, 86, 110);
        private static readonly Color Border = Color.FromArgb(211, 221, 236);
        private static readonly Color Accent = Color.FromArgb(34, 78, 194);
        private static readonly Color Error = Color.FromArgb(153, 34, 42);

        private readonly Label activeInternetValue;
        private readonly Label priorityValue;
        private readonly Label wifiValue;
        private readonly Label ethernetValue;
        private readonly Label message;
        private readonly ListView connectionList;
        private readonly Label connectionMessage;
        private readonly Button preferWifi;
        private readonly Button preferEthernet;
        private readonly ComboBox connectionPicker;
        private readonly Button preferConnection;
        private readonly Button restoreDefaults;
        private readonly Button refresh;
        private Guid? selectedAdapterId;
        private bool busy;
        private bool applying;
        private bool readingActiveInternet;

        public MainForm()
        {
            Text = "Internet Priority";
            Font = new Font("Segoe UI", 10F);
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = false;
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            BackColor = Page;
            ForeColor = Ink;

            float dpiScale;
            using (var graphics = CreateGraphics())
                dpiScale = Math.Max(1F, graphics.DpiX / 96F);
            Func<int, int> pixels = value => (int)Math.Round(value * dpiScale);
            var workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            var chromeSize = SizeFromClientSize(Size.Empty);
            ClientSize = new Size(
                Math.Min(pixels(600), Math.Max(1, workArea.Width - chromeSize.Width)),
                Math.Min(pixels(766), Math.Max(1, workArea.Height - chromeSize.Height)));
            MinimumSize = new Size(
                Math.Min(pixels(560) + chromeSize.Width, workArea.Width),
                Math.Min(pixels(420) + chromeSize.Height, workArea.Height));
            Location = new Point(
                workArea.Left + Math.Max(0, (workArea.Width - Width) / 2),
                workArea.Top + Math.Max(0, (workArea.Height - Height) / 2));

            var layout = MakeLayout(1);
            layout.Padding = new Padding(pixels(26), pixels(21), pixels(26), pixels(18));
            layout.RowCount = 9;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, pixels(78)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, pixels(196)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, pixels(16)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, pixels(49)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, pixels(48)));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, pixels(68)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, pixels(55)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, pixels(80)));

            refresh = MakeButton("&Refresh", false);
            refresh.Dock = DockStyle.None;
            refresh.Anchor = AnchorStyles.Right;
            refresh.Size = new Size(pixels(91), pixels(35));

            var header = MakeLayout(2);
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, pixels(99)));
            header.RowCount = 2;
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, pixels(44)));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, pixels(29)));
            header.Controls.Add(MakeLabel("Internet Priority", 24F, FontStyle.Bold, Ink), 0, 0);
            header.Controls.Add(refresh, 1, 0);
            var description = MakeLabel("Choose which connection Windows tries first.", 10F, FontStyle.Regular, Secondary);
            header.Controls.Add(description, 0, 1);
            header.SetColumnSpan(description, 2);
            layout.Controls.Add(header, 0, 0);

            var borderPanel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(1), BackColor = Border };
            var card = MakeLayout(2);
            card.BackColor = Color.White;
            card.Padding = new Padding(pixels(18), pixels(12), pixels(18), pixels(12));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            card.RowCount = 7;
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, pixels(22)));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, pixels(54)));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, pixels(9)));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, pixels(19)));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, pixels(26)));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, pixels(20)));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var activeInternetCaption = MakeLabel("Using for internet", 10F, FontStyle.Regular, Secondary);
            activeInternetValue = MakeLabel("Checking…", 14F, FontStyle.Bold, Ink);
            activeInternetValue.TextAlign = ContentAlignment.TopLeft;
            activeInternetValue.AutoEllipsis = false;
            const string routeExplanation = "Windows can choose a different route for other destinations. Internet reachability is unverified.";
            activeInternetValue.AccessibleDescription = routeExplanation;
            var activeInternetTip = new ToolTip();
            activeInternetTip.SetToolTip(activeInternetValue, routeExplanation);
            activeInternetValue.TextChanged += delegate
            {
                activeInternetTip.SetToolTip(activeInternetValue, activeInternetValue.Text + "\n\n" + routeExplanation);
            };
            card.Controls.Add(activeInternetCaption, 0, 0);
            card.Controls.Add(activeInternetValue, 0, 1);
            card.SetColumnSpan(activeInternetCaption, 2);
            card.SetColumnSpan(activeInternetValue, 2);

            var rule = new Panel { Dock = DockStyle.Top, Height = 1, Margin = new Padding(0, pixels(2), 0, 0), BackColor = Border };
            card.Controls.Add(rule, 0, 2);
            card.SetColumnSpan(rule, 2);

            var priorityCaption = MakeLabel("Current priority", 10F, FontStyle.Regular, Secondary);
            priorityValue = MakeLabel("Checking…", 13F, FontStyle.Bold, Ink);
            card.Controls.Add(priorityCaption, 0, 3);
            card.Controls.Add(priorityValue, 0, 4);
            card.SetColumnSpan(priorityCaption, 2);
            card.SetColumnSpan(priorityValue, 2);

            card.Controls.Add(MakeLabel("Wi-Fi", 10F, FontStyle.Bold, Ink), 0, 5);
            card.Controls.Add(MakeLabel("Ethernet", 10F, FontStyle.Bold, Ink), 1, 5);
            wifiValue = MakeLabel("Checking…", 10F, FontStyle.Regular, Secondary);
            ethernetValue = MakeLabel("Checking…", 10F, FontStyle.Regular, Secondary);
            card.Controls.Add(wifiValue, 0, 6);
            card.Controls.Add(ethernetValue, 1, 6);
            borderPanel.Controls.Add(card);
            layout.Controls.Add(borderPanel, 0, 1);

            var choices = MakeLayout(2);
            choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            choices.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            preferWifi = MakeButton("Prefer &Wi-Fi", true);
            preferWifi.Margin = new Padding(0, 0, pixels(6), 0);
            preferEthernet = MakeButton("Prefer &Ethernet", true);
            preferEthernet.Margin = new Padding(pixels(6), 0, 0, 0);
            choices.Controls.Add(preferWifi, 0, 0);
            choices.Controls.Add(preferEthernet, 1, 0);
            layout.Controls.Add(choices, 0, 3);

            message = MakeLabel("", 10F, FontStyle.Regular, Secondary);
            message.Margin = new Padding(0, pixels(4), 0, 0);
            layout.Controls.Add(message, 0, 4);

            var connections = MakeLayout(1);
            connections.RowCount = 2;
            connections.RowStyles.Add(new RowStyle(SizeType.Absolute, pixels(26)));
            connections.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            connections.Controls.Add(MakeLabel("Connections", 11F, FontStyle.Bold, Ink), 0, 0);

            var connectionPanel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            connectionList = new ListView
            {
                AccessibleName = "Connections",
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                ForeColor = Ink,
                BorderStyle = BorderStyle.FixedSingle,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                ShowItemToolTips = true,
                Visible = false
            };
            var typeColumn = connectionList.Columns.Add("Type", pixels(88));
            var nameColumn = connectionList.Columns.Add("Name", pixels(300));
            var statusColumn = connectionList.Columns.Add("Status", pixels(116));
            connectionList.SizeChanged += delegate
            {
                nameColumn.Width = Math.Max(pixels(80),
                    connectionList.ClientSize.Width - typeColumn.Width - statusColumn.Width - pixels(20));
            };
            connectionMessage = MakeLabel("Checking connections…", 10F, FontStyle.Regular, Secondary);
            connectionPanel.Controls.Add(connectionList);
            connectionPanel.Controls.Add(connectionMessage);
            connections.Controls.Add(connectionPanel, 0, 1);
            layout.Controls.Add(connections, 0, 5);

            var connectionActions = MakeLayout(3);
            connectionActions.RowCount = 2;
            connectionActions.RowStyles.Add(new RowStyle(SizeType.Absolute, pixels(24)));
            connectionActions.RowStyles.Add(new RowStyle(SizeType.Absolute, pixels(36)));
            connectionActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            connectionActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, pixels(86)));
            connectionActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, pixels(188)));
            var specificCaption = MakeLabel("Specific connection", 10F, FontStyle.Regular, Secondary);
            connectionActions.Controls.Add(specificCaption, 0, 0);
            connectionActions.SetColumnSpan(specificCaption, 3);

            connectionPicker = new ComboBox
            {
                AccessibleName = "Specific connection",
                AccessibleDescription = "Choose the Wi-Fi or Ethernet adapter to prioritise.",
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                DropDownStyle = ComboBoxStyle.DropDownList,
                DropDownWidth = pixels(340),
                FormattingEnabled = true,
                Margin = new Padding(0, 0, pixels(8), 0),
                Enabled = false,
                TabIndex = 0
            };
            connectionPicker.Items.Add("Checking connections…");
            connectionPicker.SelectedIndex = 0;
            connectionPicker.Format += delegate (object sender, ListControlConvertEventArgs args)
            {
                var connection = args.ListItem as DetectedConnection;
                if (connection == null)
                    return;

                if (!string.IsNullOrWhiteSpace(connection.AdapterAlias)
                    && !string.Equals(connection.AdapterAlias, connection.Name, StringComparison.OrdinalIgnoreCase))
                    args.Value = string.Format("{0} — {1} ({2})", connection.Name, connection.AdapterAlias, connection.Status);
                else
                    args.Value = string.Format("{0} ({1}, {2})", connection.Name, connection.Kind, connection.Status);
            };
            connectionActions.Controls.Add(connectionPicker, 0, 1);

            preferConnection = MakeButton("&Use first", true);
            preferConnection.AccessibleDescription = "Prioritise the selected adapter.";
            preferConnection.Margin = new Padding(0, 0, pixels(8), 0);
            preferConnection.Enabled = false;
            preferConnection.TabIndex = 1;
            connectionActions.Controls.Add(preferConnection, 1, 1);

            restoreDefaults = MakeButton("Restore Windows &defaults", false);
            restoreDefaults.AccessibleDescription = "Let Windows set Wi-Fi and Ethernet priority automatically.";
            restoreDefaults.Enabled = false;
            restoreDefaults.TabIndex = 2;
            connectionActions.Controls.Add(restoreDefaults, 2, 1);
            layout.Controls.Add(connectionActions, 0, 6);

            var note = MakeLabel("Changing priority updates Windows network settings and needs administrator approval.\nReconnect your VPN if it keeps using the previous connection.", 9F, FontStyle.Regular, Secondary);
            note.AutoEllipsis = false;
            layout.Controls.Add(note, 0, 7);
            layout.Controls.Add(MakeFooter(pixels), 0, 8);
            var viewport = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                AutoScrollMinSize = new Size(pixels(520), pixels(730))
            };
            viewport.Controls.Add(layout);
            Controls.Add(viewport);

            var activeInternetTimer = new Timer { Interval = 5000 };
            activeInternetTimer.Tick += async delegate { await RefreshActiveInternetAsync(); };
            Disposed += delegate
            {
                activeInternetTimer.Dispose();
                activeInternetTip.Dispose();
            };
            Shown += async delegate
            {
                activeInternetTimer.Start();
                await Task.WhenAll(RefreshActiveInternetAsync(), RunOperationAsync(null));
            };
            refresh.Click += async delegate { await RunOperationAsync(null); };
            preferWifi.Click += async delegate { await RunOperationAsync(Preference.WiFi); };
            preferEthernet.Click += async delegate { await RunOperationAsync(Preference.Ethernet); };
            connectionPicker.SelectedIndexChanged += delegate
            {
                if (busy)
                    return;

                var connection = connectionPicker.SelectedItem as DetectedConnection;
                selectedAdapterId = connection == null ? null : connection.AdapterId;
                UpdateConnectionActions();
            };
            preferConnection.Click += async delegate
            {
                var connection = connectionPicker.SelectedItem as DetectedConnection;
                if (connection != null && connection.AdapterId.HasValue)
                    await RunOperationAsync(Preference.Connection, connection.AdapterId);
            };
            restoreDefaults.Click += async delegate { await RunOperationAsync(Preference.WindowsDefaults); };
            UpdateConnectionActions();
        }

        private TableLayoutPanel MakeFooter(Func<int, int> pixels)
        {
            var footer = MakeLayout(2);
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, pixels(224)));
            footer.RowCount = 2;
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, pixels(10)));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var divider = new Panel { Dock = DockStyle.Top, Height = 1, Margin = Padding.Empty, BackColor = Border };
            footer.Controls.Add(divider, 0, 0);
            footer.SetColumnSpan(divider, 2);

            var credit = MakeLayout(1);
            credit.Padding = new Padding(0, pixels(10), 0, pixels(10));
            credit.RowCount = 2;
            credit.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            credit.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            credit.Controls.Add(MakeLabel("Built by Ryan Handler", 10F, FontStyle.Bold, Ink), 0, 0);
            var support = new LinkLabel
            {
                Text = "Report an issue",
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Font = new Font("Segoe UI", 9.5F),
                TextAlign = ContentAlignment.MiddleLeft,
                LinkColor = Accent,
                ActiveLinkColor = Ink,
                LinkBehavior = LinkBehavior.HoverUnderline,
                TabStop = true
            };
            support.LinkClicked += delegate
            {
                OpenLink("https://github.com/shwartzewolf/internet-priority/issues", "Could not open GitHub in your browser.");
            };
            credit.Controls.Add(support, 0, 1);
            footer.Controls.Add(credit, 0, 1);

            var coffee = MakeButton("Buy me a coffee to say thanks\nPayPal", false);
            coffee.AccessibleDescription = "Opens the PayPal page in your browser.";
            coffee.TextAlign = ContentAlignment.MiddleCenter;
            coffee.Margin = new Padding(pixels(7), 0, 0, pixels(2));
            coffee.Padding = new Padding(pixels(8), pixels(3), pixels(8), pixels(3));
            coffee.Click += delegate { OpenLink("https://paypal.me/RMHandler", "Could not open PayPal in your browser."); };
            footer.Controls.Add(coffee, 1, 1);
            return footer;
        }

        private void OpenLink(string destination, string failureMessage)
        {
            try
            {
                using (Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true }))
                {
                }
            }
            catch (Win32Exception)
            {
                SetMessage(failureMessage, true);
            }
            catch (InvalidOperationException)
            {
                SetMessage(failureMessage, true);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs args)
        {
            base.OnFormClosing(args);
            if (applying && args.CloseReason == CloseReason.UserClosing)
            {
                args.Cancel = true;
                SetMessage("Wait for the current change to finish.", false);
            }
        }

        private async Task RefreshActiveInternetAsync()
        {
            if (readingActiveInternet)
                return;

            readingActiveInternet = true;
            string label;
            try
            {
                label = await Task.Run(() => NetworkService.ReadActiveInternetLabel());
            }
            catch (Exception)
            {
                label = null;
            }
            finally
            {
                readingActiveInternet = false;
            }

            if (!IsDisposed)
                activeInternetValue.Text = string.IsNullOrWhiteSpace(label) ? "Route unavailable" : label;
        }

        private async Task RunOperationAsync(Preference? preferred, Guid? adapterId = null)
        {
            if (busy)
                return;

            applying = preferred.HasValue;
            SetBusy(true);
            var restoring = preferred == Preference.WindowsDefaults;
            SetMessage(restoring ? "Restoring Windows defaults…"
                : applying ? "Applying preference…" : "Checking network adapters…", false);
            var outcome = "";
            var failed = false;

            try
            {
                if (preferred.HasValue)
                {
                    try
                    {
                        await Task.Run(() => NetworkService.ApplyWithElevation(preferred.Value, adapterId));
                        outcome = restoring ? "Windows automatic priority restored." : "Preference saved.";
                    }
                    catch (OperationCanceledException)
                    {
                        outcome = "Change cancelled.";
                    }
                    catch (Exception error)
                    {
                        outcome = error.Message;
                        failed = true;
                    }
                }

                try
                {
                    var status = await Task.Run(() => NetworkService.ReadStatus());
                    if (IsDisposed)
                        return;

                    priorityValue.Text = status.PriorityLabel;
                    wifiValue.Text = status.WiFiLabel;
                    ethernetValue.Text = status.EthernetLabel;
                    ShowConnections(status.Connections);
                }
                catch (Exception error)
                {
                    if (IsDisposed)
                        return;

                    priorityValue.Text = "Status unavailable";
                    wifiValue.Text = "Unavailable";
                    ethernetValue.Text = "Unavailable";
                    ShowConnections(null);
                    if (!failed)
                        outcome = (outcome + " " + error.Message).Trim();
                    failed = true;
                }

                SetMessage(outcome, failed);
                await RefreshActiveInternetAsync();
            }
            finally
            {
                applying = false;
                if (!IsDisposed)
                    SetBusy(false);
            }
        }

        private void ShowConnections(DetectedConnection[] connections)
        {
            connectionList.BeginUpdate();
            connectionPicker.BeginUpdate();
            try
            {
                connectionList.Items.Clear();
                connectionPicker.Items.Clear();
                connectionPicker.Items.Add(connections == null ? "Connection details unavailable" : "Choose a connection…");
                int selectedIndex = 0;
                if (connections != null)
                {
                    foreach (var connection in connections)
                    {
                        var row = new ListViewItem(new[] { connection.Kind, connection.Name, connection.Status });
                        row.ToolTipText = connection.Name;
                        connectionList.Items.Add(row);

                        if (connection.AdapterId.HasValue)
                        {
                            int index = connectionPicker.Items.Add(connection);
                            if (connection.AdapterId == selectedAdapterId)
                                selectedIndex = index;
                        }
                    }
                }

                if (connections != null && connectionPicker.Items.Count == 1)
                    connectionPicker.Items[0] = "No adapters available";
                connectionPicker.SelectedIndex = selectedIndex;
            }
            finally
            {
                connectionPicker.EndUpdate();
                connectionList.EndUpdate();
            }

            bool hasConnections = connectionList.Items.Count > 0;
            connectionList.Visible = hasConnections;
            connectionMessage.Visible = !hasConnections;
            connectionMessage.Text = connections == null ? "Connection details unavailable." : "No connections detected.";
        }

        private void SetBusy(bool value)
        {
            busy = value;
            refresh.Enabled = !value;
            preferWifi.Enabled = !value;
            preferEthernet.Enabled = !value;
            preferWifi.BackColor = value ? Border : Accent;
            preferEthernet.BackColor = value ? Border : Accent;
            UpdateConnectionActions();
        }

        private void UpdateConnectionActions()
        {
            bool adaptersAvailable = connectionPicker.Items.Count > 1;
            connectionPicker.Enabled = !busy && adaptersAvailable;
            preferConnection.Enabled = !busy && connectionPicker.SelectedIndex > 0;
            preferConnection.BackColor = preferConnection.Enabled ? Accent : Border;
            restoreDefaults.Enabled = !busy && adaptersAvailable;
        }

        private void SetMessage(string text, bool failed)
        {
            message.ForeColor = failed ? Error : Secondary;
            message.Text = text;
        }

        private static TableLayoutPanel MakeLayout(int columns)
        {
            return new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                ColumnCount = columns
            };
        }

        private static Label MakeLabel(string text, float size, FontStyle style, Color color)
        {
            return new Label
            {
                Text = text,
                Font = new Font("Segoe UI", size, style),
                ForeColor = color,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                UseMnemonic = false
            };
        }

        private static Button MakeButton(string text, bool primary)
        {
            var button = new Button
            {
                Text = text,
                Font = new Font("Segoe UI", primary ? 11F : 9.5F, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                BackColor = primary ? Accent : Color.White,
                ForeColor = primary ? Color.White : Accent,
                UseVisualStyleBackColor = false
            };

            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(28, 65, 166) : Color.FromArgb(228, 237, 253);
            button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(21, 53, 139) : Color.FromArgb(217, 230, 252);
            return button;
        }
    }
}
