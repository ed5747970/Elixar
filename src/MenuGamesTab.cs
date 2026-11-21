// Tools > Right-click menu (ContextMenus.cs) and Tools > Games (Games.cs).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public partial class MainForm
    {
        // ---------- Right-click menu ----------
        TabPage pageMenu;
        ListView menuList; Label menuStatus; Button btnMenuOff, btnMenuOn, btnMenuRefresh; CheckBox chkMenuWindows;
        bool menuBusy, menuLoaded;
        public List<MenuEntry> MenuEntries = new List<MenuEntry>();   // self-test
        public event Action MenuDone;                                  // self-test
        public void MenuShowWindowsForTest(bool on) { chkMenuWindows.Checked = on; }
        public int MenuRowsForTest { get { return menuList.Items.Count; } }
        public void MenuSelectForTest(string name) { foreach (ListViewItem it in menuList.Items) it.Selected = ((MenuEntry)it.Tag).Name == name; MenuButtons(); }

        void BuildMenu()
        {
            pageMenu = new TabPage("Right-click menu") { BackColor = pageTools.BackColor, AutoScroll = true };
            var t = Page(5);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var menuIntro = Note("What apps added to the right-click menu of files, folders, drives and the desktop. An entry whose program is gone makes the menu slow or breaks it. " +
                              "Turning one off keeps it: Turn on puts it back. Windows' own entries are hidden and never changed. On Windows 11 most of these are under \"Show more options\".");
            btnMenuRefresh = MakeButton("Check again");
            chkMenuWindows = new CheckBox { AutoSize = true, Text = "Show Windows' own", Margin = new Padding(S(12), S(6), 0, 0) };
            var top = TextFit.WrapRow(new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) }); menuStatus = StatusNote(); top.Controls.AddRange(new Control[] { btnMenuRefresh, chkMenuWindows, menuStatus });
            menuList = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true, HideSelection = false };
            menuList.Columns.Add("Entry", S(230)); menuList.Columns.Add("Shows for", S(170)); menuList.Columns.Add("Kind", S(90)); menuList.Columns.Add("Program", S(340)); menuList.Columns.Add("State", S(260));
            FillLast(menuList);
            btnMenuOff = MakeButton("Turn off"); btnMenuOn = MakeButton("Turn on");
            var bottom = TextFit.WrapRow(new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(6), 0, 0) }); bottom.Controls.AddRange(new Control[] { btnMenuOff, btnMenuOn });
            t.Controls.Add(Title("Right-click menu"), 0, 0); t.Controls.Add(menuIntro, 0, 1); t.Controls.Add(top, 0, 2); t.Controls.Add(menuList, 0, 3); t.Controls.Add(bottom, 0, 4);
            pageMenu.Controls.Add(t);
            pageMenu.AutoScrollMinSize = new Size(0, S(420));
            btnMenuRefresh.Click += (o, e) => RefreshMenu();
            chkMenuWindows.CheckedChanged += (o, e) => FillMenu();
            menuList.SelectedIndexChanged += (o, e) => MenuButtons();
            btnMenuOff.Click += (o, e) => SetMenuOff(true);
            btnMenuOn.Click += (o, e) => SetMenuOff(false);
            MenuButtons();
        }

        void MenuButtons()
        {
            var sel = menuList.SelectedItems.Cast<ListViewItem>().Select(i => (MenuEntry)i.Tag).ToList();
            bool can = !menuBusy && Policy.Allowed("menu.change");
            btnMenuOff.Enabled = can && sel.Any(e => !e.WindowsOwn && !e.Off);
            btnMenuOn.Enabled = can && sel.Any(e => !e.WindowsOwn && e.Off);
        }

        public void RefreshMenu()
        {
            if (menuBusy) return;
            menuBusy = true; menuLoaded = true; MenuButtons();
            Task.Factory.StartNew(() => ContextMenus.Read()).ContinueWith(tk => UI(() =>
            {
                menuBusy = false;
                if (tk.IsFaulted) { menuStatus.Text = Lang.T("The check stopped: ") + tk.Exception.GetBaseException().Message; MenuButtons(); return; }
                MenuEntries = tk.Result; FillMenu();
                int broken = MenuEntries.Count(e => e.Missing && !e.Off), apps = MenuEntries.Count(e => !e.WindowsOwn);
                menuStatus.Text = Lang.T(apps + " entries added by apps" + (broken > 0 ? ", " + broken + " broken (in orange): turn them off." : ", none broken."));
                MenuButtons();
                if (MenuDone != null) MenuDone();
            }));
        }

        void FillMenu()
        {
            menuList.BeginUpdate(); menuList.Items.Clear();
            foreach (var e in MenuEntries.Where(x => chkMenuWindows.Checked || !x.WindowsOwn))
            {
                var it = new ListViewItem(NameText.Plain(e.Name)) { Tag = e, ToolTipText = e.Hive + "\\...\\" + e.Key + (e.Clsid.Length > 0 ? "\n" + e.Clsid : "") };
                it.SubItems.Add(string.Join(", ", e.Where.Split(new[] { ", " }, StringSplitOptions.None).Select(w => Lang.T(w))));
                it.SubItems.Add(Lang.T(e.Kind == "Handler" ? "Add-in" : "Command"));
                it.SubItems.Add(e.Program.Length > 0 ? e.Program : "-");
                it.SubItems.Add(Lang.T(e.Why));
                var c = LevelColor(e.Level); if (c != Color.Empty) it.ForeColor = c; if (e.Off) it.ForeColor = Color.Gray;
                menuList.Items.Add(it);
            }
            menuList.EndUpdate();
        }

        void SetMenuOff(bool off)
        {
            var sel = menuList.SelectedItems.Cast<ListViewItem>().Select(i => (MenuEntry)i.Tag).Where(e => !e.WindowsOwn && e.Off != off).ToList();
            var errs = new List<string>();
            foreach (var e in sel) { string err = ContextMenus.SetOff(e, off); if (err != null) errs.Add(e.Name + ": " + Lang.T(err)); else Log("Right-click menu: " + e.Name + (off ? " turned off." : " turned on.")); }
            FillMenu(); MenuButtons();
            menuStatus.Text = errs.Count > 0 ? string.Join("  ", errs) : Lang.T(off ? "Turned off. It goes from the menu after you sign out or restart File Explorer." : "Turned on again.");
        }

        // ---------- Games ----------
        TabPage pageGames;
        ListView gamesList; Label gameStatus; Button btnGameStore, btnGameUninstall, btnGameOpen, btnGameRefresh;
        bool gameBusy, gameLoaded;
        public List<Game> GameRows = new List<Game>();   // self-test
        public event Action GamesDone;                   // self-test

        void BuildGames()
        {
            pageGames = new TabPage("Games") { BackColor = pageTools.BackColor, AutoScroll = true };
            var t = Page(5);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var gameIntro = Note("Games from Steam, Epic, GOG and the Xbox app, biggest first, with when each was last played (Steam keeps that). " +
                              "Elixar never deletes or moves a game itself: each store does that properly, so the buttons open the game in its store.");
            btnGameRefresh = MakeButton("Check again");
            var top = TextFit.WrapRow(new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) }); gameStatus = StatusNote(); top.Controls.AddRange(new Control[] { btnGameRefresh, gameStatus });
            gamesList = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true, MultiSelect = false, HideSelection = false };
            gamesList.Columns.Add("Game", S(260)); gamesList.Columns.Add("Store", S(80)); gamesList.Columns.Add("Size", S(90), HorizontalAlignment.Right); gamesList.Columns.Add("Last played", S(220));
            gamesList.Columns.Add("Drive", S(60)); gamesList.Columns.Add("To move it to another drive", S(380));
            FillLast(gamesList);
            btnGameStore = MakeButton("Open in its store"); btnGameUninstall = MakeButton("Uninstall in Steam..."); btnGameOpen = MakeButton("Open folder");
            var bottom = TextFit.WrapRow(new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(6), 0, 0) });
            bottom.Controls.AddRange(new Control[] { btnGameStore, btnGameUninstall, btnGameOpen });
            t.Controls.Add(Title("Games"), 0, 0); t.Controls.Add(gameIntro, 0, 1); t.Controls.Add(top, 0, 2); t.Controls.Add(gamesList, 0, 3); t.Controls.Add(bottom, 0, 4);
            pageGames.Controls.Add(t);
            pageGames.AutoScrollMinSize = new Size(0, S(420));
            btnGameRefresh.Click += (o, e) => RefreshGames();
            gamesList.SelectedIndexChanged += (o, e) => GameButtons();
            Func<Game> sel = () => gamesList.SelectedItems.Count > 0 ? gamesList.SelectedItems[0].Tag as Game : null;
            btnGameStore.Click += (o, e) => { var g = sel(); string l = g == null ? null : Games.StoreLink(g); if (l != null && !testMode) try { Shell.Open(l); } catch (Exception ex) { gameStatus.Text = Lang.T("Couldn't open it: " + ex.Message); } };
            btnGameUninstall.Click += (o, e) => { var g = sel(); string l = g == null ? null : Games.UninstallLink(g); if (l != null && !testMode) try { Shell.Open(l); } catch (Exception ex) { gameStatus.Text = Lang.T("Couldn't open it: " + ex.Message); } };
            btnGameOpen.Click += (o, e) => { var g = sel(); if (g != null && !testMode && Directory.Exists(g.Folder) && (File.GetAttributes(g.Folder) & FileAttributes.Directory) != 0) try { Shell.Open(g.Folder.TrimEnd('\\') + "\\"); } catch { } };   // only a real folder (a store's list could name a program)
            GameButtons();
        }

        void GameButtons()
        {
            var g = gamesList.SelectedItems.Count > 0 ? gamesList.SelectedItems[0].Tag as Game : null;
            btnGameStore.Enabled = g != null && Games.StoreLink(g) != null;
            btnGameUninstall.Enabled = g != null && Games.UninstallLink(g) != null;
            btnGameOpen.Enabled = g != null;
            btnGameRefresh.Enabled = !gameBusy;
        }

        public void RefreshGames()
        {
            if (gameBusy) return;
            gameBusy = true; gameLoaded = true; GameButtons();
            gameStatus.Text = Lang.T("Looking for games and measuring them...");
            Task.Factory.StartNew(() => Games.Read(true), TaskCreationOptions.LongRunning).ContinueWith(tk => UI(() =>
            {
                gameBusy = false;
                if (tk.IsFaulted) { gameStatus.Text = Lang.T("The check stopped: ") + tk.Exception.GetBaseException().Message; GameButtons(); return; }
                GameRows = tk.Result;
                gamesList.BeginUpdate(); gamesList.Items.Clear();
                foreach (var g in GameRows)
                {
                    var it = new ListViewItem(NameText.Plain(g.Name)) { Tag = g, ToolTipText = NameText.Plain(g.Folder) };
                    it.SubItems.Add(g.Store); it.SubItems.Add(g.Bytes > 0 ? Fs.Fmt(g.Bytes) : "-");
                    it.SubItems.Add(g.LastPlayed.HasValue ? g.LastPlayed.Value.ToString("d") + (g.Unplayed(DateTime.Now) ? "  " + Lang.T("(over 6 months ago)") : "") : "-");
                    it.SubItems.Add(g.Drive); it.SubItems.Add(Lang.T(Games.HowToMove(g.Store)));
                    if (g.Unplayed(DateTime.Now)) it.ForeColor = LevelColor(CheckLevel.Warn);
                    gamesList.Items.Add(it);
                }
                if (GameRows.Count == 0) gamesList.Items.Add(new ListViewItem(Lang.T("No games found from Steam, Epic, GOG or the Xbox app.")));
                gamesList.EndUpdate();
                long total = GameRows.Sum(g => g.Bytes), old = GameRows.Where(g => g.Unplayed(DateTime.Now)).Sum(g => g.Bytes);
                if (GameRows.Count > 0)
                    gameStatus.Text = Lang.T(GameRows.Count + " games, " + Fs.Fmt(total) + (old > 0 ? "; " + Fs.Fmt(old) + " in games not played for over 6 months (in orange)." : "."));
                GameButtons();
                if (GamesDone != null) GamesDone();
            }));
        }
    }
}
