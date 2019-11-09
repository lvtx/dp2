﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Xml;
using System.IO;

using dp2SSL.Models;
using static dp2SSL.LibraryChannelUtil;
using dp2SSL.Dialog;

using DigitalPlatform;
using DigitalPlatform.Core;
using DigitalPlatform.LibraryClient;
using DigitalPlatform.RFID;
using DigitalPlatform.Interfaces;
using DigitalPlatform.Text;
using DigitalPlatform.IO;
using DigitalPlatform.LibraryClient.localhost;
using DigitalPlatform.LibraryServer;

namespace dp2SSL
{
    /// <summary>
    /// PageShelf.xaml 的交互逻辑
    /// </summary>
    public partial class PageShelf : Page, INotifyPropertyChanged
    {
        LayoutAdorner _adorner = null;
        AdornerLayer _layer = null;

        // EntityCollection _entities = new EntityCollection();
        Patron _patron = new Patron();

        public string Mode { get; set; }    // 运行模式。空/initial

        public PageShelf()
        {
            InitializeComponent();

            _patronErrorTable = new ErrorTable((e) =>
            {
                _patron.Error = e;
            });

            Loaded += PageShelf_Loaded;
            Unloaded += PageShelf_Unloaded;

            this.DataContext = this;

            // this.booksControl.SetSource(_entities);
            this.patronControl.DataContext = _patron;
            this.patronControl.InputFace += PatronControl_InputFace;

            this._patron.PropertyChanged += _patron_PropertyChanged;

            this.doorControl.OpenDoor += DoorControl_OpenDoor;

            App.CurrentApp.PropertyChanged += CurrentApp_PropertyChanged;



            // this.error.Text = "test";
        }

        // parameters:
        //      mode    空字符串或者“initial”
        public PageShelf(string mode) : this()
        {
            this.Mode = mode;
        }

        private async void PageShelf_Loaded(object sender, RoutedEventArgs e)
        {
            // _firstInitial = false;

            FingerprintManager.SetError += FingerprintManager_SetError;
            FingerprintManager.Touched += FingerprintManager_Touched;

            App.CurrentApp.TagChanged += CurrentApp_TagChanged;

            // RfidManager.ListLocks += RfidManager_ListLocks;
            ShelfData.OpenCountChanged += CurrentApp_OpenCountChanged;
            //ShelfData.BookChanged += ShelfData_BookChanged;

            RfidManager.ClearCache();
            // 注：将来也许可以通过(RFID 以外的)其他方式输入图书号码
            if (string.IsNullOrEmpty(RfidManager.Url))
                this.SetGlobalError("rfid", "尚未配置 RFID 中心 URL");

            _layer = AdornerLayer.GetAdornerLayer(this.mainGrid);
            _adorner = new LayoutAdorner(this);

            {
                List<string> style = new List<string>();
                if (string.IsNullOrEmpty(App.RfidUrl) == false)
                    style.Add("rfid");
                if (string.IsNullOrEmpty(App.FingerprintUrl) == false)
                    style.Add("fingerprint");
                if (string.IsNullOrEmpty(App.FaceUrl) == false)
                    style.Add("face");
                this.patronControl.SetStartMessage(StringUtil.MakePathList(style));
            }

            /*
            try
            {
                RfidManager.LockCommands = DoorControl.GetLockCommands();
            }
            catch (Exception ex)
            {
                this.SetGlobalError("cfg", $"获得门锁命令时出错:{ex.Message}");
            }
            */

            // 要在初始化以前设定好
            // RfidManager.AntennaList = GetAntennaList();

            // _patronReaderName = GetPatronReaderName();

            if (Mode == "initial" || ShelfData.FirstInitialized == false)
            {
                // TODO: 可否放到 App 的初始化阶段? 这样好处是菜单画面就可以看到有关数量显示了
                await InitialShelfEntities();

                // 迫使图书盘点暂停(如果门是全部关闭的话)
                // SetOpenCount(_openCount);

            }
        }

        private void ShowBookInfo(object sender, OpenDoorEventArgs e)
        {
            // 书柜外的读卡器触发观察图书信息对话框
            // if (e.Door.Type == "free" && e.Adds != null && e.Adds.Count > 0)
            {
                BookInfoWindow bookInfoWindow = null;

                EntityCollection collection = null;
                if (e.ButtonName == "count")
                    collection = e.Door.AllEntities;
                else if (e.ButtonName == "add")
                    collection = e.Door.AddEntities;
                else if (e.ButtonName == "remove")
                    collection = e.Door.RemoveEntities;
                else if (e.ButtonName == "errorCount")
                    collection = e.Door.ErrorEntities;

                Application.Current.Dispatcher.Invoke(new Action(() =>
                {
                    bookInfoWindow = new BookInfoWindow();
                    bookInfoWindow.TitleText = e.ButtonName;
                    bookInfoWindow.Owner = Application.Current.MainWindow;
                    bookInfoWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                    bookInfoWindow.Width = Math.Min(1800, this.ActualWidth);
                    bookInfoWindow.Height = Math.Min(700, this.ActualHeight);
                    bookInfoWindow.Closed += BookInfoWindow_Closed;
                    bookInfoWindow.SetBooks(collection);
                    bookInfoWindow.Show();
                    AddLayer();
                }));
            }
        }

        private void BookInfoWindow_Closed(object sender, EventArgs e)
        {
            RemoveLayer();
        }

        // 当前读者卡状态是否 OK?
        bool IsPatronOK(string action, out string message)
        {
            message = "";

            // 如果 UID 为空，而 Barcode 有内容，也是 OK 的。这是指纹的场景
            if (string.IsNullOrEmpty(_patron.UID) == true
                && string.IsNullOrEmpty(_patron.Barcode) == false)
                return true;

            // UID 和 Barcode 都不为空。这是 15693 和 14443 读者卡的场景
            if (string.IsNullOrEmpty(_patron.UID) == false
    && string.IsNullOrEmpty(_patron.Barcode) == false)
                return true;

            string debug_info = $"uid:[{_patron.UID}],barcode:[{_patron.Barcode}]";
            if (action == "open")
            {
                // 提示信息要考虑到应用了指纹的情况
                if (string.IsNullOrEmpty(App.FingerprintUrl) == false)
                    message = $"请先刷读者卡，或扫入一次指纹，然后再开门\r\n({debug_info})";
                else
                    message = $"请先刷读者卡，然后再开门\r\n({debug_info})";
            }
            else
            {
                // 调试用
                message = $"读卡器上的当前读者卡状态不正确。无法进行 {action} 操作\r\n({debug_info})";
            }
            return false;
        }

        void DisplayError(ref ProgressWindow progress,
    string message,
    string color = "red")
        {
            MemoryDialog(progress);
            var temp = progress;
            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                temp.MessageText = message;
                temp.BackColor = color;
                temp = null;
            }));
            progress = null;
        }

        void DisplayMessage(ProgressWindow progress,
            string message,
            string color = "")
        {
            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                progress.MessageText = message;
                if (string.IsNullOrEmpty(color) == false)
                    progress.BackColor = color;
            }));
        }

        List<Window> _dialogs = new List<Window>();

        void CloseDialogs()
        {
            // 确保 page 关闭时对话框能自动关闭
            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                foreach (var window in _dialogs)
                {
                    window.Close();
                }
            }));
        }

        void MemoryDialog(Window dialog)
        {
            _dialogs.Add(dialog);
        }

        void ErrorBox(string message, 
            string color = "red",
            string style = "")
        {
            ProgressWindow progress = null;

            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                progress = new ProgressWindow();
                progress.MessageText = "正在处理，请稍候 ...";
                progress.Owner = Application.Current.MainWindow;
                progress.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                progress.Closed += Progress_Closed;
                if (StringUtil.IsInList("button_ok", style))
                    progress.okButton.Content = "确定";
                progress.Show();
                AddLayer();
            }));


            if (StringUtil.IsInList("auto_close", style))
            {
                DisplayMessage(progress, message, color);

                Task.Run(() =>
                {
                    // TODO: 显示倒计时计数？
                    Task.Delay(TimeSpan.FromSeconds(1)).Wait();
                    Application.Current.Dispatcher.Invoke(new Action(() =>
                    {
                        progress.Close();
                    }));
                });
            }
            else
                DisplayError(ref progress, message, color);
        }

        private void DoorControl_OpenDoor(object sender, OpenDoorEventArgs e)
        {
            // 观察图书详情
            if (string.IsNullOrEmpty(e.ButtonName) == false)
            {
                ShowBookInfo(sender, e);
                return;
            }

            // 没有门锁的门
            if (string.IsNullOrEmpty(e.Door.LockName))
            {
                ErrorBox("没有门锁");
                return;
            }

            // 检查门锁是否已经是打开状态?
            if (e.Door.State == "open")
            {
                App.CurrentApp.Speak("已经打开");
                ErrorBox("已经打开", "yellow", "auto_close,button_ok");
                return;
            }

            // 以前积累的 _adds 和 _removes 要先处理，处理完再开门

            // 先检查当前是否具备读者身份？
            // 检查读者卡状态是否 OK
            if (IsPatronOK("open", out string check_message) == false)
            {
                if (string.IsNullOrEmpty(check_message))
                    check_message = $"(读卡器上的)当前读者卡状态不正确。无法进行开门操作";

                /*
                ProgressWindow progress = null;

                Application.Current.Dispatcher.Invoke(new Action(() =>
                {
                    progress = new ProgressWindow();
                    progress.MessageText = "正在处理，请稍候 ...";
                    progress.Owner = Application.Current.MainWindow;
                    progress.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                    progress.Closed += Progress_Closed;
                    progress.Show();
                    AddLayer();
                }));

                DisplayError(ref progress, check_message);
                */
                ErrorBox(check_message);
                return;
            }

            // 检查读者记录状态
            XmlDocument readerdom = new XmlDocument();
            readerdom.LoadXml(_patron.Xml);
            // return:
            //      -1  检查过程出错
            //      0   状态不正常
            //      1   状态正常
            int nRet = LibraryServerUtil.CheckPatronState(readerdom,
                out string strError);
            if (nRet != 1)
            {
                ErrorBox(check_message);
                return;
            }

            // MessageBox.Show(e.Name);
            var result = RfidManager.OpenShelfLock(e.Door.LockName, e.Door.LockIndex);
            if (result.Value == -1)
                MessageBox.Show(result.ErrorInfo);
        }

        private void CurrentApp_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Error")
            {
                OnPropertyChanged(e.PropertyName);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        void OnPropertyChanged(string name)
        {
            if (this.PropertyChanged != null)
                PropertyChanged(this, new PropertyChangedEventArgs(name));
        }

        public string Error
        {
            get
            {
                return App.CurrentApp.Error;
            }
        }


        private void CurrentApp_OpenCountChanged(object sender, OpenCountChangedEventArgs e)
        {
            // 如果从有门打开的状态变为全部门都关闭的状态，要尝试提交一次出纳请求
            if (e.OldCount > 0 && e.NewCount == 0)
            {
                SubmitCheckInOut();
                PatronClear(false);  // 确保在没有可提交内容的情况下也自动清除读者信息
            }
        }

#if NO
        int _openCount = 0; // 当前处于打开状态的门的个数

        private void RfidManager_ListLocks(object sender, ListLocksEventArgs e)
        {
            if (e.Result.Value == -1)
                return;

            // bool triggerAllClosed = false;
            {
                int count = 0;
                foreach (var state in e.Result.States)
                {
                    if (state.State == "open")
                        count++;
                    var result = DoorItem.SetLockState(_doors, state);
                    if (result.LockName != null && result.OldState != null && result.NewState != null)
                    {
                        if (result.NewState != result.OldState)
                        {
                            if (result.NewState == "open")
                                App.CurrentApp.Speak($"{result.LockName} 打开");
                            else
                                App.CurrentApp.Speak($"{result.LockName} 关闭");
                        }
                    }
                }

                //if (_openCount > 0 && count == 0)
                //    triggerAllClosed = true;

                SetOpenCount(count);
            }
        }

        // 设置打开门数量
        void SetOpenCount(int count)
        {
            int oldCount = _openCount;

            _openCount = count;

            // 打开门的数量发生变化
            if (oldCount != _openCount)
            {
                /*
                if (_openCount == 0)
                {
                    // 关闭图书读卡器(只使用读者证读卡器)
                    if (string.IsNullOrEmpty(_patronReaderName) == false
                        && RfidManager.ReaderNameList != _patronReaderName)
                    {
                        RfidManager.ReaderNameList = _patronReaderName;
                        RfidManager.ClearCache();
                    }
                }
                else
                {
                    // 打开图书读卡器(同时也使用读者证读卡器)
                    if (RfidManager.ReaderNameList != "*")
                    {
                        RfidManager.ReaderNameList = "*";
                        RfidManager.ClearCache();
                    }
                }*/
                if (oldCount > 0 && count == 0)
                {
                    // TODO: 如果从有门打开的状态变为全部门都关闭的状态，要尝试提交一次出纳请求
                    // if (triggerAllClosed)
                    {
                        SubmitCheckInOut();
                        PatronClear(false);  // 确保在没有可提交内容的情况下也自动清除读者信息
                    }
                }

            }
        }
#endif

        /*
        LockChanged SetLockState(LockState state)
        {
            return this.doorControl.SetLockState(state);
        }
        */

        private void PageShelf_Unloaded(object sender, RoutedEventArgs e)
        {
            RfidManager.SetError -= RfidManager_SetError;

            App.CurrentApp.TagChanged -= CurrentApp_TagChanged;
            //ShelfData.BookChanged -= ShelfData_BookChanged;

            FingerprintManager.Touched -= FingerprintManager_Touched;
            FingerprintManager.SetError -= FingerprintManager_SetError;

            // RfidManager.ListLocks -= RfidManager_ListLocks;
            ShelfData.OpenCountChanged -= CurrentApp_OpenCountChanged;

            // 确保 page 关闭时对话框能自动关闭
            CloseDialogs();
            PatronClear(true);
        }

        // 从指纹阅读器获取消息(第一阶段)
        private async void FingerprintManager_Touched(object sender, TouchedEventArgs e)
        {
            SetPatronInfo(e.Result);

            await FillPatronDetail();

#if NO
            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                _patron.IsFingerprintSource = true;
                _patron.Barcode = "test1234";
            }));
#endif
        }

        // 从指纹阅读器获取消息(第一阶段)
        void SetPatronInfo(GetMessageResult result)
        {
            if (result.Value == -1)
            {
                SetPatronError("fingerprint", $"指纹中心出错: {result.ErrorInfo}, 错误码: {result.ErrorCode}");
                if (_patron.IsFingerprintSource)
                    PatronClear(true);    // 只有当面板上的读者信息来源是指纹仪时，才清除面板上的读者信息
                return;
            }
            else
            {
                // 清除以前残留的报错信息
                SetPatronError("fingerprint", "");
            }

            if (result.Message == null)
                return;

            PatronClear(true);
            _patron.IsFingerprintSource = true;
            _patron.PII = result.Message;
        }


        private void FingerprintManager_SetError(object sender, SetErrorEventArgs e)
        {
            SetGlobalError("fingerprint", e.Error);
        }

        private void RfidManager_SetError(object sender, SetErrorEventArgs e)
        {
            SetGlobalError("rfid", e.Error);
            /*
            if (e.Error == null)
            {
                // 恢复正常
            }
            else
            {
                // 进入错误状态
                if (_rfidState != "error")
                {
                    await ClearBooksAndPatron(null);
                }

                _rfidState = "error";
            }
            */
        }

#if NO
        async Task<NormalResult> Update(
            BaseChannel<IRfid> channel_param,
            List<Entity> update_entities,
            CancellationToken token)
        {
            if (update_entities.Count > 0)
            {
                try
                {
                    BaseChannel<IRfid> channel = channel_param;
                    if (channel == null)
                        channel = RfidManager.GetChannel();
                    try
                    {
                        await FillBookFields(channel, update_entities, token);
                    }
                    finally
                    {
                        if (channel_param == null)
                            RfidManager.ReturnChannel(channel);
                    }
                }
                catch (Exception ex)
                {
                    string error = $"填充图书信息时出现异常: {ex.Message}";
                    SetGlobalError("rfid", error);
                    return new NormalResult { Value = -1, ErrorInfo = error };
                }

                // 自动检查 EAS 状态
                // CheckEAS(update_entities);
            }
            return new NormalResult();
        }

#endif

        // 设置全局区域错误字符串
        void SetGlobalError(string type, string error)
        {
            if (error != null && error.StartsWith("未"))
                throw new Exception("test");
            App.CurrentApp.SetError(type, error);
        }

        // 第二阶段：填充图书信息的 PII 和 Title 字段
        async Task FillBookFields(BaseChannel<IRfid> channel,
            List<Entity> entities,
            CancellationToken token)
        {
            try
            {
                foreach (Entity entity in entities)
                {
                    if (token.IsCancellationRequested)
                        return;

                    if (entity.FillFinished == true)
                        continue;

                    // 获得 PII
                    // 注：如果 PII 为空，文字中要填入 "(空)"
                    if (string.IsNullOrEmpty(entity.PII))
                    {
                        if (entity.TagInfo == null)
                            continue;

                        Debug.Assert(entity.TagInfo != null);

                        LogicChip chip = LogicChip.From(entity.TagInfo.Bytes,
(int)entity.TagInfo.BlockSize,
"" // tag.TagInfo.LockStatus
);
                        string pii = chip.FindElement(ElementOID.PII)?.Text;
                        entity.PII = PageBorrow.GetCaption(pii);
                    }

                    // 获得 Title
                    // 注：如果 Title 为空，文字中要填入 "(空)"
                    if (string.IsNullOrEmpty(entity.Title)
                        && string.IsNullOrEmpty(entity.PII) == false && entity.PII != "(空)")
                    {
                        GetEntityDataResult result = await
                            Task<GetEntityDataResult>.Run(() =>
                            {
                                return GetEntityData(entity.PII);
                            });

                        if (result.Value == -1)
                        {
                            entity.SetError(result.ErrorInfo);
                            continue;
                        }
                        entity.Title = PageBorrow.GetCaption(result.Title);
                        entity.SetData(result.ItemRecPath, result.ItemXml);
                    }

                    entity.SetError(null);
                    entity.FillFinished = true;
                }

                booksControl.SetBorrowable();
            }
            catch (Exception ex)
            {
                SetGlobalError("current", $"FillBookFields exception: {ex.Message}");
            }
        }

        // 初始化时列出当前馆藏地应有的全部图书
        // 本函数中，只给 Entity 对象里面设置好了 PII，其他成员尚未设置
        static void FillLocationBooks(EntityCollection entities,
            string location,
            CancellationToken token)
        {
            var channel = App.CurrentApp.GetChannel();
            try
            {
                long lRet = channel.SearchItem(null,
                    "<全部>",
                    location,
                    5000,
                    "馆藏地点",
                    "exact",
                    "zh",
                    "shelfResultset",
                    "",
                    "",
                    out string strError);
                if (lRet == -1)
                    throw new ChannelException(channel.ErrorCode, strError);

                string strStyle = "id,cols,format:@coldef:*/barcode|*/borrower";

                ResultSetLoader loader = new ResultSetLoader(channel,
                    null,
                    "shelfResultset",
                    strStyle,
                    "zh");
                foreach (DigitalPlatform.LibraryClient.localhost.Record record in loader)
                {
                    token.ThrowIfCancellationRequested();
                    string pii = record.Cols[0];
                    Application.Current.Dispatcher.Invoke(new Action(() =>
                    {
                        entities.Add(pii);
                    }));
                }
            }
            finally
            {
                App.CurrentApp.ReturnChannel(channel);
            }
        }

        private async void CurrentApp_TagChanged(object sender, TagChangedEventArgs e)
        {
            // 读者。不再精细的进行增删改跟踪操作，而是笼统地看 TagList.Patrons 集合即可
            var task = RefreshPatrons();

            await ShelfData.ChangeEntities((BaseChannel<IRfid>)sender, e);

            // "initial" 模式下，立即合并到 _all。等关门时候一并提交请求
            // TODO: 不过似乎此时有语音提示放入、取出，似乎更显得实用一些？
            if (this.Mode == "initial")
            {
                List<Entity> adds = new List<Entity>(ShelfData.Adds);
                foreach (var entity in adds)
                {
                    ShelfData.Add(ShelfData.All, entity);

                    ShelfData.Remove(ShelfData.Adds, entity);
                    ShelfData.Remove(ShelfData.Removes, entity);
                }

                List<Entity> removes = new List<Entity>(ShelfData.Removes);
                foreach (var entity in removes)
                {
                    ShelfData.Remove(ShelfData.All, entity);

                    ShelfData.Remove(ShelfData.Adds, entity);
                    ShelfData.Remove(ShelfData.Removes, entity);
                }

                ShelfData.RefreshCount();
            }
        }

        bool _initialCancelled = false;

        // 初始化开始前，要先把 RfidManager.ReaderNameList 设置为 "*"
        // 初始化完成前，先不要允许(开关门变化导致)修改 RfidManager.ReaderNameList
        async Task InitialShelfEntities()
        {
            if (ShelfData.FirstInitialized)
                return;

            this.doorControl.Visibility = Visibility.Collapsed;
            _initialCancelled = false;

            ProgressWindow progress = null;
            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                progress = new ProgressWindow();
                progress.MessageText = "正在初始化图书信息，请稍候 ...";
                progress.Owner = Application.Current.MainWindow;
                progress.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                progress.Closed += Progress_Cancelled;
                //progress.Width = 700;
                //progress.Height = 500;
                progress.okButton.Content = "取消";
                progress.Show();
                AddLayer();
            }));
            this.doorControl.Visibility = Visibility.Hidden;

            try
            {
                await ShelfData.InitialShelfEntities(
                    (s) =>
                    {
                        DisplayMessage(progress, s, "green");
                    },
                    () =>
                    {
                        return _initialCancelled;
                    });

                if (_initialCancelled)
                    return;

#if NO
                // TODO: 出现“正在初始化”的对话框。另外需要注意如果 DataReady 信号永远来不了怎么办
                await Task.Run(() =>
                {
                    TagList.DataReady = false;
                    // TODO: 是否一开始主动把 RfidManager ReaderNameList 设置为 "*"?
                    while (true)
                    {
                        if (TagList.DataReady == true)
                            return true;
                        Thread.Sleep(100);
                    }
                });

                _all.Clear();
                var books = TagList.Books;
                foreach (var tag in books)
                {
                    _all.Add(NewEntity(tag));
                }

                // DoorItem.DisplayCount(_all, _adds, _removes, App.CurrentApp.Doors);
                ShelfData.RefreshCount();
#endif
                // 把门显示出来。因为此时需要看到是否关门的状态
                this.doorControl.Visibility = Visibility.Visible;
                this.doorControl.InitializeButtons(ShelfData.ShelfCfgDom, ShelfData.Doors);

                // 检查门是否为关闭状态？
                // 注意 RfidManager 中门锁启动需要一定时间。状态可能是：尚未初始化/有门开着/门都关了
                await Task.Run(() =>
                {
                    while (ShelfData.OpeningDoorCount > 0)
                    {
                        if (_initialCancelled)
                            break;
                        DisplayMessage(progress, "请关闭全部柜门，以完成初始化", "yellow");
                        Thread.Sleep(1000);
                    }
                });

                if (_initialCancelled)
                    return;

                // 此时门是关闭状态。让读卡器切换到节省盘点状态
                ShelfData.RefreshReaderNameList();

                TryReturn(progress, ShelfData.All);

                if (_initialCancelled)
                    return;

                /*
                if (_initialCancelled == false)
                {
                    this.doorControl.Visibility = Visibility.Visible;
                }
                */
            }
            finally
            {
                // _firstInitial = true;   // 第一次初始化已经完成
                RemoveLayer();

                if (_initialCancelled == false)
                {
                    // PageMenu.MenuPage.shelf.Visibility = Visibility.Visible;

                    progress.Closed -= Progress_Cancelled;
                    Application.Current.Dispatcher.Invoke(new Action(() =>
                    {
                        if (progress != null)
                            progress.Close();
                    }));

                    SetGlobalError("initial", null);
                    this.Mode = ""; // 从初始化模式转为普通模式
                }
                else
                {
                    ShelfData.FirstInitialized = false;

                    // PageMenu.MenuPage.shelf.Visibility = Visibility.Collapsed;

                    // TODO: 页面中央大字显示“书柜初始化失败”。重新进入页面时候应该自动重试初始化
                    SetGlobalError("initial", "智能书柜初始化失败。请检查读卡器和门锁参数配置，重新进行初始化 ...");
                    /*
                    ProgressWindow error = null;
                    Application.Current.Dispatcher.Invoke(new Action(() =>
                    {
                        error = new ProgressWindow();
                        error.Owner = Application.Current.MainWindow;
                        error.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                        error.Closed += Error_Closed;
                        error.Show();
                        AddLayer();
                    }));
                    DisplayError(ref error, "智能书柜初始化失败。请检查读卡器和门锁参数配置，重新进行初始化 ...");
                    */
                }
            }

            // TODO: 初始化中断后，是否允许切换到菜单和设置画面？(只是不让进入书架画面)
        }

        private void Error_Closed(object sender, EventArgs e)
        {
            RemoveLayer();
        }

        // 初始化被中途取消
        private void Progress_Cancelled(object sender, EventArgs e)
        {
            _initialCancelled = true;
        }

        // 刷新读者信息
        // TODO: 当读者信息更替时，要检查前一个读者是否有 _adds 和 _removes 队列需要提交，先提交，再刷成后一个读者信息
        async Task RefreshPatrons()
        {
            //_lock_refreshPatrons.EnterWriteLock();
            try
            {
                var patrons = TagList.Patrons;
                if (patrons.Count == 1)
                    _patron.IsRfidSource = true;

                if (_patron.IsFingerprintSource)
                {
                    // 指纹仪来源
                }
                else
                {
                    // RFID 来源
                    if (patrons.Count == 1)
                    {
                        if (_patron.Fill(patrons[0].OneTag) == false)
                            return;

                        SetPatronError("rfid_multi", "");   // 2019/5/22

                        // 2019/5/29
                        await FillPatronDetail();
                    }
                    else
                    {
                        // 拿走 RFID 读者卡时，不要清除读者信息。也就是说和指纹做法一样

                        // PatronClear(false); // 不需要 submit


                        SetPatronError("getreaderinfo", "");
                        if (patrons.Count > 1)
                        {
                            // 读卡器上放了多张读者卡
                            SetPatronError("rfid_multi", $"读卡器上放了多张读者卡({patrons.Count})。请拿走多余的");
                        }
                        else
                            SetPatronError("rfid_multi", "");   // 2019/5/20
                    }
                }
            }
            finally
            {
                //_lock_refreshPatrons.ExitWriteLock();
            }
        }

        // 填充读者信息的其他字段(第二阶段)
        async Task<NormalResult> FillPatronDetail(bool force = false)
        {
            // 已经填充过了
            if (_patron.PatronName != null
                && force == false)
                return new NormalResult();

            string pii = _patron.PII;
            if (string.IsNullOrEmpty(pii))
                pii = _patron.UID;

            if (string.IsNullOrEmpty(pii))
                return new NormalResult();

            // return.Value:
            //      -1  出错
            //      0   读者记录没有找到
            //      1   成功
            GetReaderInfoResult result = await
                Task<GetReaderInfoResult>.Run(() =>
                {
                    return GetReaderInfo(pii);
                });

            if (result.Value != 1)
            {
                string error = $"读者 '{pii}': {result.ErrorInfo}";
                SetPatronError("getreaderinfo", error);
                return new NormalResult { Value = -1, ErrorInfo = error };
            }

            SetPatronError("getreaderinfo", "");

            //if (string.IsNullOrEmpty(_patron.State) == true)
            //    OpenDoor();

            // TODO: 出现一个半透明(倒计时)提示对话框，提示可以开门了。如果书柜只有一个门，则直接打开这个门？

            if (force)
                _patron.PhotoPath = "";
            // string old_photopath = _patron.PhotoPath;
            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                _patron.SetPatronXml(result.RecPath, result.ReaderXml, result.Timestamp);
                this.patronControl.SetBorrowed(result.ReaderXml);
            }));

            // 显示在借图书列表
            List<Entity> entities = new List<Entity>();
            foreach (Entity entity in this.patronControl.BorrowedEntities)
            {
                entities.Add(entity);
            }
            if (entities.Count > 0)
            {
                try
                {
                    BaseChannel<IRfid> channel = RfidManager.GetChannel();
                    try
                    {
                        await FillBookFields(channel, entities, new CancellationToken());
                    }
                    finally
                    {
                        RfidManager.ReturnChannel(channel);
                    }
                }
                catch (Exception ex)
                {
                    string error = $"填充读者信息时出现异常: {ex.Message}";
                    SetGlobalError("rfid", error);
                    return new NormalResult { Value = -1, ErrorInfo = error };
                }
            }
#if NO
            // 装载图象
            if (old_photopath != _patron.PhotoPath)
            {
                Task.Run(()=> {
                    LoadPhoto(_patron.PhotoPath);
                });
            }
#endif
            return new NormalResult();
        }

        // parameters:
        //      submitBefore    是否自动提交前面残留的 _adds 和 _removes ?
        void PatronClear(bool submitBefore)
        {
            // 预先提交一次
            if (submitBefore)
            {
                if (ShelfData.Adds.Count > 0 || ShelfData.Removes.Count > 0)
                    SubmitCheckInOut(false);
            }

            _patron.Clear();

            if (this.patronControl.BorrowedEntities.Count == 0)
            {
                if (!Application.Current.Dispatcher.CheckAccess())
                    Application.Current.Dispatcher.Invoke(new Action(() =>
                    {
                        this.patronControl.BorrowedEntities.Clear();
                    }));
                else
                    this.patronControl.BorrowedEntities.Clear();
            }
        }

        #region patron 分类报错机制

        // 错误类别 --> 错误字符串
        // 错误类别有：rfid fingerprint getreaderinfo
        ErrorTable _patronErrorTable = null;

        // 设置读者区域错误字符串
        void SetPatronError(string type, string error)
        {
            _patronErrorTable.SetError(type, error);
        }

        #endregion

        bool _visiblityChanged = false;

        private void _patron_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "PhotoPath")
            {
                Task.Run(() =>
                {
                    try
                    {
                        this.patronControl.LoadPhoto(_patron.PhotoPath);
                    }
                    catch (Exception ex)
                    {
                        SetGlobalError("patron", ex.Message);
                    }
                });
            }

            if (e.PropertyName == "UID"
                || e.PropertyName == "Barcode")
            {
                // 如果 patronControl 本来是隐藏状态，但读卡器上放上了读者卡，这时候要把 patronControl 恢复显示
                if ((string.IsNullOrEmpty(_patron.UID) == false || string.IsNullOrEmpty(_patron.Barcode) == false)
                    && this.patronControl.Visibility != Visibility.Visible)
                    Application.Current.Dispatcher.Invoke(new Action(() =>
                    {
                        patronControl.Visibility = Visibility.Visible;
                        _visiblityChanged = true;
                    }));
                // 如果读者卡又被拿走了，则要恢复 patronControl 的隐藏状态
                else if (string.IsNullOrEmpty(_patron.UID) == true && string.IsNullOrEmpty(_patron.Barcode) == true
    && this.patronControl.Visibility == Visibility.Visible
    && _visiblityChanged)
                    Application.Current.Dispatcher.Invoke(new Action(() =>
                    {
                        patronControl.Visibility = Visibility.Collapsed;
                    }));
            }
        }

        /*
        // 开门
        NormalResult OpenDoor()
        {
            // 打开对话框，询问门号
            OpenDoorWindow progress = null;

            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                progress = new OpenDoorWindow();
                // progress.MessageText = "正在处理，请稍候 ...";
                progress.Owner = Application.Current.MainWindow;
                progress.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                progress.Closed += Progress_Closed;
                progress.Show();
                AddLayer();
            }));

            try
            {
                progress = null;

                return new NormalResult();
            }
            finally
            {
                Application.Current.Dispatcher.Invoke(new Action(() =>
                {
                    if (progress != null)
                        progress.Close();
                }));
            }
        }
        */

        private void Progress_Closed(object sender, EventArgs e)
        {
            RemoveLayer();
        }

        void AddLayer()
        {
            _layer.Add(_adorner);
        }

        void RemoveLayer()
        {
            _layer.Remove(_adorner);
        }

        private async void GoHome_Click(object sender, RoutedEventArgs e)
        {
            // 检查全部门是否关闭

            if (ShelfData.OpeningDoorCount > 0)
            {
                ErrorBox("请先关闭全部柜门，才能返回菜单页面");
                return;
            }

            /*
            await Task.Run(() =>
            {
                while (ShelfData.OpeningDoorCount > 0)
                {
                    if (_initialCancelled)
                        break;
                    DisplayMessage(progress, "请先关闭全部柜门，以返回菜单页面", "yellow");
                    Thread.Sleep(1000);
                }
            });
            */

            this.NavigationService.Navigate(PageMenu.MenuPage);
        }

        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            // OpenDoor();
        }

        // TODO: 报错信息尝试用 FlowDocument 改造
        // 尝试进行一次还书操作
        void TryReturn(ProgressWindow progress,
            List<Entity> entities)
        {
            List<ActionInfo> actions = new List<ActionInfo>();
            foreach (var entity in entities)
            {
                actions.Add(new ActionInfo { Entity = entity, Action = "return" });
            }

            LibraryChannel channel = App.CurrentApp.GetChannel();
            try
            {
                Application.Current.Dispatcher.Invoke(new Action(() =>
                {
                    progress.ProgressBar.Value = 0;
                    progress.ProgressBar.Minimum = 0;
                    progress.ProgressBar.Maximum = actions.Count;
                }));

                // TODO: 准备工作：把涉及到的 Entity 对象的字段填充完整
                // 检查 PII 是否都具备了

                int skip_count = 0;
                int success_count = 0;
                List<string> errors = new List<string>();
                List<string> borrows = new List<string>();
                List<string> returns = new List<string>();
                foreach (ActionInfo info in actions)
                {
                    string action = info.Action;
                    Entity entity = info.Entity;

                    string action_name = "借书";
                    if (action == "return")
                        action_name = "还书";
                    else if (action == "renew")
                        action_name = "续借";

                    long lRet = 0;
                    string strError = "";
                    string[] item_records = null;
                    string[] biblio_records = null;

                    if (action == "return")
                    {
                        // 智能书柜不使用 EAS 状态。可以考虑统一修改为 EAS Off 状态？

                        entity.Waiting = true;
                        lRet = channel.Return(null,
                            "return",
                            _patron.Barcode,
                            entity.PII,
                            entity.ItemRecPath,
                            false,
                            "item,reader,biblio", // style,
                            "xml", // item_format_list
                            out item_records,
                            "xml",
                            out string[] reader_records,
                            "summary",
                            out biblio_records,
                            out string[] dup_path,
                            out string output_reader_barcode,
                            out ReturnInfo return_info,
                            out strError);
                    }

                    Application.Current.Dispatcher.Invoke(new Action(() =>
                    {
                        progress.ProgressBar.Value++;
                    }));

                    if (biblio_records != null && biblio_records.Length > 0)
                        entity.Title = biblio_records[0];

                    string title = entity.PII;
                    if (string.IsNullOrEmpty(entity.Title) == false)
                        title += " (" + entity.Title + ")";

                    // TODO: 各种情况的返回值和错误码
                    if (lRet == -1)
                    {
                        /*
                        // return 操作如果 API 失败，则要改回原来的 EAS 状态
                        if (action == "return")
                        {
                            var result = SetEAS(entity.UID, entity.Antenna, false);
                            if (result.Value == -1)
                                strError += $"\r\n并且复原 EAS 状态的动作也失败了: {result.ErrorInfo}";
                        }
                        */

                        if (channel.ErrorCode == ErrorCode.NotBorrowed)
                        {

                        }
                        else
                        {
                            entity.SetError($"{action_name}操作失败: {strError}", "red");
                            // TODO: 这里最好用 title
                            errors.Add($"册 '{title}': {strError}");
                        }
                        continue;
                    }

                    if (action == "borrow")
                        borrows.Add(title);
                    if (action == "return")
                        returns.Add(title);

                    // TODO: 把 _adds 和 _removes 归入 _all
                    // 是否一边处理一边动态修改 _all?
                    if (action == "return")
                        ShelfData.Add(ShelfData.All, entity);
                    else
                        ShelfData.Remove(ShelfData.All, entity);

                    ShelfData.Remove(ShelfData.Adds, entity);
                    ShelfData.Remove(ShelfData.Removes, entity);

                    /*
                    // borrow 操作，API 之后才修改 EAS
                    // 注: 如果 API 成功但修改 EAS 动作失败(可能由于读者从读卡器上过早拿走图书导致)，读者会无法把本册图书拿出门禁。遇到此种情况，读者回来补充修改 EAS 一次即可
                    if (action == "borrow")
                    {
                        var result = SetEAS(entity.UID, entity.Antenna, action == "return");
                        if (result.Value == -1)
                        {
                            entity.SetError($"虽然{action_name}操作成功，但修改 EAS 动作失败: {result.ErrorInfo}", "yellow");
                            errors.Add($"册 '{entity.PII}' {action_name}操作成功，但修改 EAS 动作失败: {result.ErrorInfo}");
                        }
                    }
                    */

                    // 刷新显示
                    {
                        if (item_records?.Length > 0)
                            entity.SetData(entity.ItemRecPath, item_records[0]);

                        if (entity.Error != null)
                            continue;

                        string message = $"{action_name}成功";
                        if (lRet == 1 && string.IsNullOrEmpty(strError) == false)
                            message = strError;
                        entity.SetError(message,
                            lRet == 1 ? "yellow" : "green");
                        success_count++;
                        // 刷新显示。特别是一些关于借阅日期，借期，应还日期的内容
                    }
                }

                Application.Current.Dispatcher.Invoke(new Action(() =>
                {
                    progress.ProgressBar.Visibility = Visibility.Collapsed;
                    // progress.ProgressBar.Value = progress.ProgressBar.Maximum;
                }));

                // 修改 borrowable
                // booksControl.SetBorrowable();

                if (errors.Count > 0)
                {
                    string error = StringUtil.MakePathList(errors, "\r\n");
                    string message = $"操作出错 {errors.Count} 个";
                    if (success_count > 0)
                        message += $"，成功 {success_count} 个";
                    if (skip_count > 0)
                        message += $" (另有 {skip_count} 个被忽略)";

                    if (errors.Count > 0)
                        message += $"\r\n出错:\r\n" + error;

                    DisplayError(ref progress, message);
                    App.CurrentApp.Speak(message);
                    return; // new NormalResult { Value = -1, ErrorInfo = StringUtil.MakePathList(errors, "; ") };
                }
                else
                {
                    /*
                    // 成功
                    string backColor = "green";
                    string message = $"{patron_name} 操作成功 {success_count} 笔";
                    string speak = $"出纳完成";

                    if (skip_count > 0)
                        message += $" (另有 {skip_count} 笔被忽略)";
                    if (skip_count > 0 && success_count == 0)
                    {
                        backColor = "yellow";
                        message = $"全部 {skip_count} 笔出纳操作被忽略";
                        speak = $"出纳失败";
                    }
                    if (skip_count == 0 && success_count == 0)
                    {
                        backColor = "yellow";
                        message = $"请先把图书放到读卡器上，再进行 出纳 操作";
                        speak = $"出纳失败";
                    }

                    if (returns.Count > 0)
                        message += $"\r\n还书:\r\n" + MakeList(returns);

                    if (borrows.Count > 0)
                        message += $"\r\n借书:\r\n" + MakeList(borrows);

                    DisplayError(ref progress, message, backColor);

                    // 重新装载读者信息和显示
                    // var task = FillPatronDetail(true);
                    this.doorControl.DisplayCount(_all, _adds, _removes);

                    App.CurrentApp.Speak(speak);
                    */
                }

                return; // new NormalResult { Value = success_count };
            }
            finally
            {
                App.CurrentApp.ReturnChannel(channel);
            }
        }

        class ActionInfo
        {
            public Entity Entity { get; set; }
            public string Action { get; set; }  // borrow/return
        }

        // 关门，或者更换读者的时候，向服务器提交出纳请求
        // parameters:
        //      clearPatron 操作完成后是否自动清除右侧的读者信息
        void SubmitCheckInOut(bool clearPatron = true)
        {
            // TODO: 如果当前没有读者身份，则当作初始化处理，将书柜内的全部图书做还书尝试；被拿走的图书记入本地日志(所谓无主操作)
            // TODO: 注意还书，也就是往书柜里面放入图书，是不需要具体读者身份就可以提交的

            // TODO: 属于 free 类型的门里面的图书不要参与处理

            List<ActionInfo> actions = new List<ActionInfo>();
            foreach (var entity in ShelfData.Adds)
            {
                if (ShelfData.BelongToNormal(entity) == false)
                    continue;
                actions.Add(new ActionInfo { Entity = entity, Action = "return" });
            }
            foreach (var entity in ShelfData.Removes)
            {
                if (ShelfData.BelongToNormal(entity) == false)
                    continue;
                actions.Add(new ActionInfo { Entity = entity, Action = "borrow" });
            }

            if (actions.Count == 0)
                return;

            ProgressWindow progress = null;
            string patron_name = "";
            patron_name = _patron.PatronName;

            Application.Current.Dispatcher.Invoke(new Action(() =>
            {

                progress = new ProgressWindow();
                progress.MessageText = "正在处理，请稍候 ...";
                progress.Owner = Application.Current.MainWindow;
                progress.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                progress.Closed += Progress_Closed;
                progress.Width = Math.Min(700, this.ActualWidth);
                progress.Height = Math.Min(500, this.ActualHeight);
                progress.Show();
                AddLayer();
            }));

            // 先尽量执行还书请求，再报错说无法进行借书操作(记入错误日志)
            MessageDocument doc = new MessageDocument();

            bool patron_filled = false;

            // 检查读者卡状态是否 OK
            if (IsPatronOK("open", out string check_message) == false)
            {
                /*
                if (string.IsNullOrEmpty(check_message))
                    check_message = $"读卡器上的当前读者卡状态不正确。无法进行 checkin/out 操作";

                DisplayError(ref progress, check_message);
                return;
                */
            }
            else
                patron_filled = true;

            LibraryChannel channel = App.CurrentApp.GetChannel();
            try
            {
                // ClearEntitiesError();

                Application.Current.Dispatcher.Invoke(new Action(() =>
                {
                    progress.ProgressBar.Value = 0;
                    progress.ProgressBar.Minimum = 0;
                    progress.ProgressBar.Maximum = actions.Count;
                }));

                // TODO: 准备工作：把涉及到的 Entity 对象的字段填充完整
                // 检查 PII 是否都具备了

                int skip_count = 0;
                int success_count = 0;
                List<string> errors = new List<string>();
                List<string> borrows = new List<string>();
                List<string> returns = new List<string>();
                List<string> warnings = new List<string>();
                foreach (ActionInfo info in actions)
                {
                    string action = info.Action;
                    Entity entity = info.Entity;

                    string action_name = "借书";
                    if (action == "return")
                        action_name = "还书";
                    else if (action == "renew")
                        action_name = "续借";

                    // 借书操作必须要有读者卡。(还书和续借，可要可不要)
                    if (action == "borrow")
                    {
                        if (patron_filled == false)
                        {
                            // 界面警告
                            errors.Add($"册 '{entity.PII}' 无法进行借书请求");
                            // 写入错误日志
                            WpfClientInfo.WriteInfoLog($"册 '{entity.PII}' 无法进行借书请求");
                            continue;
                        }

                        if (string.IsNullOrEmpty(_patron.Barcode))
                        {
                            DisplayError(ref progress, $"请先在读卡器上放好读者卡，再进行{action_name}");
                            return;
                        }
                    }

                    long lRet = 0;
                    string strError = "";
                    string[] item_records = null;
                    string[] biblio_records = null;
                    BorrowInfo borrow_info = null;

                    if (action == "borrow" || action == "renew")
                    {
                        /*
                        if (action == "borrow" && entity.State == "borrowed")
                        {
                            entity.SetError($"本册是外借状态。{action_name}操作被忽略", "yellow");
                            skip_count++;
                            continue;
                        }
                        if (action == "renew" && entity.State == "onshelf")
                        {
                            entity.SetError($"本册是在馆状态。{action_name}操作被忽略 (只有处于外借状态的册才能进行续借)", "yellow");
                            skip_count++;
                            continue;
                        }
                        */
                        // TODO: 智能书柜要求强制借书。如果册操作前处在被其他读者借阅状态，要自动先还书再进行借书

                        entity.Waiting = true;
                        lRet = channel.Borrow(null,
                            action == "renew",
                            _patron.Barcode,
                            entity.PII,
                            entity.ItemRecPath,
                            false,
                            null,
                            "item,reader,biblio,overflowable", // style,
                            "xml", // item_format_list
                            out item_records,
                            "xml",
                            out string[] reader_records,
                            "summary",
                            out biblio_records,
                            out string[] dup_path,
                            out string output_reader_barcode,
                            out borrow_info,
                            out strError);

                    }
                    else if (action == "return")
                    {
                        /*
                        if (entity.State == "onshelf")
                        {
                            entity.SetError($"本册是在馆状态。{action_name}操作被忽略", "yellow");
                            skip_count++;
                            continue;
                        }
                        */

                        /*
                        // TODO: 增加检查 EAS 现有状态功能，如果已经是 true 则不用修改，后面 API 遇到出错后也不要回滚 EAS
                        // return 操作，提前修改 EAS
                        // 注: 提前修改 EAS 的好处是比较安全。相比 API 执行完以后再修改 EAS，提前修改 EAS 成功后，无论后面发生什么，读者都无法拿着这本书走出门禁
                        {
                            var result = SetEAS(entity.UID, entity.Antenna, action == "return");
                            if (result.Value == -1)
                            {
                                entity.SetError($"{action_name}时修改 EAS 动作失败: {result.ErrorInfo}", "red");
                                errors.Add($"册 '{entity.PII}' {action_name}时修改 EAS 动作失败: {result.ErrorInfo}");
                                continue;
                            }
                        }
                        */
                        // 智能书柜不使用 EAS 状态。可以考虑统一修改为 EAS Off 状态？

                        entity.Waiting = true;
                        lRet = channel.Return(null,
                            "return",
                            "", // _patron.Barcode,
                            entity.PII,
                            entity.ItemRecPath,
                            false,
                            "item,reader,biblio", // style,
                            "xml", // item_format_list
                            out item_records,
                            "xml",
                            out string[] reader_records,
                            "summary",
                            out biblio_records,
                            out string[] dup_path,
                            out string output_reader_barcode,
                            out ReturnInfo return_info,
                            out strError);
                    }

                    Application.Current.Dispatcher.Invoke(new Action(() =>
                    {
                        progress.ProgressBar.Value++;
                    }));

                    if (biblio_records != null && biblio_records.Length > 0)
                        entity.Title = biblio_records[0];

                    string title = entity.PII;
                    if (string.IsNullOrEmpty(entity.Title) == false)
                        title += " (" + entity.Title + ")";

                    {
                        // 把 _adds 和 _removes 归入 _all
                        // 一边处理一边动态修改 _all?
                        if (action == "return")
                            ShelfData.Add(ShelfData.All, entity);
                        else
                            ShelfData.Remove(ShelfData.All, entity);

                        ShelfData.Remove(ShelfData.Adds, entity);
                        ShelfData.Remove(ShelfData.Removes, entity);
                    }

                    string resultType = "succeed";
                    if (lRet == -1)
                        resultType = "error";
                    else if (lRet == 1)
                        resultType = "information";
                    MessageItem messageItem = new MessageItem
                    {
                        Operation = action,
                        ResultType = resultType,
                        ErrorCode = channel.ErrorCode.ToString(),
                        ErrorInfo = strError,
                        Entity = entity,
                    };
                    doc.Add(messageItem);

                    // 微调
                    if (lRet == 0 && action == "return")
                        messageItem.ErrorInfo = "";

                    if (lRet == -1)
                    {
                        /*
                        // return 操作如果 API 失败，则要改回原来的 EAS 状态
                        if (action == "return")
                        {
                            var result = SetEAS(entity.UID, entity.Antenna, false);
                            if (result.Value == -1)
                                strError += $"\r\n并且复原 EAS 状态的动作也失败了: {result.ErrorInfo}";
                        }
                        */

                        if (action == "return")
                        {
                            if (channel.ErrorCode == ErrorCode.NotBorrowed)
                            {
                                // TODO: 这里不知是普通状态还是 warning 合适。warning 是否比较强烈了
                                messageItem.ResultType = "warning";
                                messageItem.ErrorCode = ErrorCode.NotBorrowed.ToString();
                                // 界面警告
                                warnings.Add($"册 '{title}' (尝试还书时发现未曾被借出过): {strError}");
                                // 写入错误日志
                                WpfClientInfo.WriteInfoLog($"读者 {_patron.NameSummary} 尝试还回册 '{title}' 时: {strError}");
                                continue;
                            }
                        }

                        entity.SetError($"{action_name}操作失败: {strError}", "red");
                        // TODO: 这里最好用 title
                        errors.Add($"册 '{title}': {strError}");
                        continue;
                    }

                    if (action == "borrow")
                    {
                        if (borrow_info.Overflows != null && borrow_info.Overflows.Length > 0)
                        {
                            // 界面警告
                            // TODO: 可以考虑归入 overflows 单独语音警告处理。语音要简洁。详细原因可出现在文字警告中
                            // warnings.Add($"册 '{title}' (借书操作发生溢出，请于当日内还书): {string.Join("; ", borrow_info.Overflows)}");

                            // TODO: 详细原因文字可否用稍弱的字体效果来显示？
                            messageItem.ErrorInfo = $"借书操作超越许可，请将本册放回书柜。详细原因： {string.Join("; ", borrow_info.Overflows)}";
                            messageItem.ResultType = "warning";
                            messageItem.ErrorCode = "overflow";
                            // 写入错误日志
                            WpfClientInfo.WriteInfoLog($"读者 {_patron.NameSummary} 借阅 '{title}' 时发生超越许可: {strError}");
                        }
                    }

                    if (action == "borrow")
                        borrows.Add(title);
                    if (action == "return")
                        returns.Add(title);

                    /*
                    // borrow 操作，API 之后才修改 EAS
                    // 注: 如果 API 成功但修改 EAS 动作失败(可能由于读者从读卡器上过早拿走图书导致)，读者会无法把本册图书拿出门禁。遇到此种情况，读者回来补充修改 EAS 一次即可
                    if (action == "borrow")
                    {
                        var result = SetEAS(entity.UID, entity.Antenna, action == "return");
                        if (result.Value == -1)
                        {
                            entity.SetError($"虽然{action_name}操作成功，但修改 EAS 动作失败: {result.ErrorInfo}", "yellow");
                            errors.Add($"册 '{entity.PII}' {action_name}操作成功，但修改 EAS 动作失败: {result.ErrorInfo}");
                        }
                    }
                    */

                    // 刷新显示
                    {
                        if (item_records?.Length > 0)
                            entity.SetData(entity.ItemRecPath, item_records[0]);

                        if (entity.Error != null)
                            continue;

                        string message = $"{action_name}成功";
                        if (lRet == 1 && string.IsNullOrEmpty(strError) == false)
                            message = strError;
                        entity.SetError(message,
                            lRet == 1 ? "yellow" : "green");
                        success_count++;
                        // 刷新显示。特别是一些关于借阅日期，借期，应还日期的内容
                    }
                }

                Application.Current.Dispatcher.Invoke(new Action(() =>
                {
                    progress.ProgressBar.Visibility = Visibility.Collapsed;
                    // progress.ProgressBar.Value = progress.ProgressBar.Maximum;
                }));

                // 修改 borrowable
                // booksControl.SetBorrowable();

#if NO
                if (errors.Count > 0)
                {
                    // TODO: 成功和出错可能会同时存在

                    string error = StringUtil.MakePathList(errors, "\r\n");
                    string message = $"操作出错 {errors.Count} 个";
                    if (success_count > 0)
                        message += $"，成功 {success_count} 个";
                    if (skip_count > 0)
                        message += $" (另有 {skip_count} 个被忽略)";

                    if (errors.Count > 0)
                        message += $"\r\n出错:\r\n" + error;

                    DisplayError(ref progress, message);
                    App.CurrentApp.Speak(message);
                    return; // new NormalResult { Value = -1, ErrorInfo = StringUtil.MakePathList(errors, "; ") };
                }
                else
#endif
                {
#if NO
                    // 成功
                    string backColor = "green";
                    string message = "";

                    if (success_count > 0)
                        message = $"{patron_name} 操作成功 {success_count} 笔";
                    if (errors.Count > 0)
                    {
                        message += "\r\n";
                        message += $"操作出错 {errors.Count} 个";

                        backColor = "red";
                    }
                    if (warnings.Count > 0)
                    {
                        message += "\r\n";
                        message += $"操作警告 {warnings.Count} 个";

                        backColor = "yellow";
                    }

                    string speak = $"出纳完成";

                    /*
                    if (skip_count > 0)
                    {
                        message += "\r\n";
                        message += $" (另有 {skip_count} 笔被忽略)";
                    }

                    if (skip_count > 0 && success_count == 0)
                    {
                        backColor = "yellow";
                        message = $"全部 {skip_count} 笔出纳操作被忽略";
                        speak = $"出纳失败";
                    }
                    if (skip_count == 0 && success_count == 0)
                    {
                        backColor = "yellow";
                        message = $"请先把图书放到读卡器上，再进行 出纳 操作";
                        speak = $"出纳失败";
                    }
                    */

                    if (errors.Count > 0)
                        message += $"\r\n出错:\r\n" + MakeList(errors);

                    if (warnings.Count > 0)
                        message += $"\r\n警告:\r\n" + MakeList(warnings);

                    if (returns.Count > 0)
                        message += $"\r\n还书:\r\n" + MakeList(returns);

                    if (borrows.Count > 0)
                        message += $"\r\n借书:\r\n" + MakeList(borrows);
#endif
                    string speak = "";
                    Application.Current.Dispatcher.Invoke(new Action(() =>
                    {
                        // DisplayError(ref progress, message, backColor);
                        progress.MessageDocument = doc.BuildDocument(patron_name, 18, out speak);
                        progress = null;
                    }));

                    // 重新装载读者信息和显示
                    // DoorItem.DisplayCount(_all, _adds, _removes, App.CurrentApp.Doors);
                    ShelfData.RefreshCount();

                    App.CurrentApp.Speak(speak);
                }

                return; // new NormalResult { Value = success_count };
            }
            finally
            {
                App.CurrentApp.ReturnChannel(channel);
                Application.Current.Dispatcher.Invoke(new Action(() =>
                {
                    if (progress != null)
                        progress.Close();
                }));
                if (clearPatron)
                    PatronClear(false);
            }
        }

        static string MakeList(List<string> list)
        {
            StringBuilder text = new StringBuilder();
            int i = 1;
            foreach (string s in list)
            {
                text.Append($"{i++}) {s}\r\n");
            }

            return text.ToString();
        }

        // 延时自动清除读者信息
        // 当在规定的时间内没有打开柜门，则自动清除读者信息。若打开了则不会清除
        async Task DelayClearPatron(CancellationToken token)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            PatronClear(true);
        }

        #region 人脸识别功能

        bool _stopVideo = false;

        private async void PatronControl_InputFace(object sender, EventArgs e)
        {
            RecognitionFaceResult result = null;

            VideoWindow videoRecognition = null;
            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                videoRecognition = new VideoWindow
                {
                    TitleText = "识别人脸 ...",
                    Owner = Application.Current.MainWindow,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                videoRecognition.Closed += VideoRecognition_Closed;
                videoRecognition.Show();
            }));
            _stopVideo = false;
            var task = Task.Run(() =>
            {
                DisplayVideo(videoRecognition);
            });
            try
            {
                result = await RecognitionFace("");
                if (result.Value == -1)
                {
                    if (result.ErrorCode != "cancelled")
                        SetGlobalError("face", result.ErrorInfo);
                    DisplayError(ref videoRecognition, result.ErrorInfo);
                    return;
                }

                SetGlobalError("face", null);
            }
            finally
            {
                if (videoRecognition != null)
                    Application.Current.Dispatcher.Invoke(new Action(() =>
                    {
                        videoRecognition.Close();
                    }));
            }

            GetMessageResult message = new GetMessageResult
            {
                Value = 1,
                Message = result.Patron,
            };
            SetPatronInfo(message);
            SetQuality("");
            await FillPatronDetail();
        }

        void DisplayError(ref VideoWindow videoRegister,
        string message,
        string color = "red")
        {
            MemoryDialog(videoRegister);
            var temp = videoRegister;
            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                temp.MessageText = message;
                temp.BackColor = color;
                temp.okButton.Content = "返回";
                temp = null;
            }));
            videoRegister = null;
        }


        void SetQuality(string text)
        {
            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                this.Quality.Text = text;
            }));
        }

        void DisplayVideo(VideoWindow window)
        {
            while (_stopVideo == false)
            {
                var result = FaceManager.GetImage("");
                if (result.ImageData == null)
                {
                    Thread.Sleep(500);
                    continue;
                }
                MemoryStream stream = new MemoryStream(result.ImageData);
                try
                {
                    Application.Current.Dispatcher.Invoke(new Action(() =>
                    {
                        window.SetPhoto(stream);
                    }));
                    stream = null;
                }
                finally
                {
                    if (stream != null)
                        stream.Close();
                }
            }
        }

        private void VideoRecognition_Closed(object sender, EventArgs e)
        {
            FaceManager.CancelRecognitionFace();
            _stopVideo = true;
            RemoveLayer();
        }

        void EnableControls(bool enable)
        {
            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                //this.borrowButton.IsEnabled = enable;
                //this.returnButton.IsEnabled = enable;
                this.goHome.IsEnabled = enable;
                this.patronControl.inputFace.IsEnabled = enable;
            }));
        }

        async Task<RecognitionFaceResult> RecognitionFace(string style)
        {
            EnableControls(false);
            try
            {
                return await Task.Run<RecognitionFaceResult>(() =>
                {
                    // 2019/9/6 增加
                    var result = FaceManager.GetState("camera");
                    if (result.Value == -1)
                        return new RecognitionFaceResult
                        {
                            Value = -1,
                            ErrorInfo = result.ErrorInfo,
                            ErrorCode = result.ErrorCode
                        };
                    return FaceManager.RecognitionFace("");
                });
            }
            finally
            {
                EnableControls(true);
            }
        }

        #endregion
    }
}
