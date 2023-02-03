using Newtonsoft.Json;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.ComponentModel;
using System.Net;

namespace MCLauncher {
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.IO.Compression;
    using System.Threading;
    using System.Windows.Data;
    using Windows.ApplicationModel;
    using Windows.Foundation;
    using Windows.Management.Core;
    using Windows.Management.Deployment;
    using Windows.System;
    using WPFDataTypes;

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, ICommonVersionCommands {

        private static readonly string PREFS_PATH = @"preferences.json";
        private static readonly string IMPORTED_VERSIONS_PATH = @"imported_versions";
        private static readonly string VERSIONS_API = "https://raw.fastgit.org/MCMrARM/mc-w10-versiondb/master/versions.json.min";

        private VersionList _versions;
        public Preferences UserPrefs { get; }

        private HashSet<CollectionViewSource> _versionListViews = new HashSet<CollectionViewSource>();

        private readonly VersionDownloader _anonVersionDownloader = new VersionDownloader();
        private readonly VersionDownloader _userVersionDownloader = new VersionDownloader();
        private readonly Task _userVersionDownloaderLoginTask;
        private volatile int _userVersionDownloaderLoginTaskStarted;
        private volatile bool _hasLaunchTask = false;

        public MainWindow() {
            _versions = new VersionList("versions.json", IMPORTED_VERSIONS_PATH, VERSIONS_API, this, VersionEntryPropertyChanged);
            InitializeComponent();
            ShowInstalledVersionsOnlyCheckbox.DataContext = this;


            if (File.Exists(PREFS_PATH)) {
                UserPrefs = JsonConvert.DeserializeObject<Preferences>(File.ReadAllText(PREFS_PATH));
            } else {
                UserPrefs = new Preferences();
                RewritePrefs();
            }

            var versionListViewRelease = Resources["versionListViewRelease"] as CollectionViewSource;
            versionListViewRelease.Filter += new FilterEventHandler((object sender, FilterEventArgs e) => {
                var v = e.Item as Version;
                e.Accepted = v.VersionType == VersionType.Release && (v.IsInstalled || v.IsStateChanging || !(ShowInstalledVersionsOnlyCheckbox.IsChecked ?? false));
            });
            versionListViewRelease.Source = _versions;
            ReleaseVersionList.DataContext = versionListViewRelease;
            _versionListViews.Add(versionListViewRelease);

            var versionListViewBeta = Resources["versionListViewBeta"] as CollectionViewSource;
            versionListViewBeta.Filter += new FilterEventHandler((object sender, FilterEventArgs e) => {
                var v = e.Item as Version;
                e.Accepted = v.VersionType == VersionType.Beta && (v.IsInstalled || v.IsStateChanging || !(ShowInstalledVersionsOnlyCheckbox.IsChecked ?? false));
            });
            versionListViewBeta.Source = _versions;
            BetaVersionList.DataContext = versionListViewBeta;
            _versionListViews.Add(versionListViewBeta);

            var versionListViewPreview = Resources["versionListViewPreview"] as CollectionViewSource;
            versionListViewPreview.Filter += new FilterEventHandler((object sender, FilterEventArgs e) => {
                var v = e.Item as Version;
                e.Accepted = v.VersionType == VersionType.Preview && (v.IsInstalled || v.IsStateChanging || !(ShowInstalledVersionsOnlyCheckbox.IsChecked ?? false));
            });
            versionListViewPreview.Source = _versions;
            PreviewVersionList.DataContext = versionListViewPreview;
            _versionListViews.Add(versionListViewPreview);

            var versionListViewImported = Resources["versionListViewImported"] as CollectionViewSource;
            versionListViewImported.Filter += new FilterEventHandler((object sender, FilterEventArgs e) => {
                var v = e.Item as Version;
                e.Accepted = v.VersionType == VersionType.Imported;
            });

            versionListViewImported.Source = _versions;
            ImportedVersionList.DataContext = versionListViewImported;
            _versionListViews.Add(versionListViewImported);

            _userVersionDownloaderLoginTask = new Task(() => {
                _userVersionDownloader.EnableUserAuthorization();
            });
            Dispatcher.Invoke(LoadVersionList);
        }

        private async void LoadVersionList() {
            LoadingProgressLabel.Content = "加载来自缓存的版本";
            LoadingProgressBar.Value = 1;

            LoadingProgressGrid.Visibility = Visibility.Visible;

            try {
                await _versions.LoadFromCache();
            } catch (Exception e) {
                Debug.WriteLine("列表缓存加载失败:\n" + e.ToString());
            }

            LoadingProgressLabel.Content = "从 " + VERSIONS_API + "更新版本列表";
            LoadingProgressBar.Value = 2;
            try {
                await _versions.DownloadList();
            } catch (Exception e) {
                Debug.WriteLine("版本列表下载失败:\n" + e.ToString());
                MessageBox.Show("从网上更新版本列表失败。一些新版本可能丢失。\n", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            LoadingProgressLabel.Content = "加载导入的版本";
            LoadingProgressBar.Value = 3;
            await _versions.LoadImported();

            LoadingProgressGrid.Visibility = Visibility.Collapsed;
        }

        private void VersionEntryPropertyChanged(object sender, PropertyChangedEventArgs e) {
            RefreshLists();
        }

        private async void ImportButtonClicked(object sender, RoutedEventArgs e) {
            Microsoft.Win32.OpenFileDialog openFileDlg = new Microsoft.Win32.OpenFileDialog();
            openFileDlg.Filter = "UWP 安装包 (*.appx)|*.appx|所有文件|*.*";
            Nullable<bool> result = openFileDlg.ShowDialog();
            if (result == true) {
                string directory = Path.Combine(IMPORTED_VERSIONS_PATH, openFileDlg.SafeFileName);
                if (Directory.Exists(directory)) {
                    var found = false;
                    foreach (var version in _versions) {
                        if (version.IsImported && version.GameDirectory == directory) {
                            if (version.IsStateChanging) {
                                MessageBox.Show("已有一个同名的版本被导入，目前正在修改中。请稍候再试。", "错误");
                                return;
                            }
                            MessageBoxResult messageBoxResult = System.Windows.MessageBox.Show("已有一个同名的版本被导入。你想删除它吗？", "删除确认", System.Windows.MessageBoxButton.YesNo);
                            if (messageBoxResult == MessageBoxResult.Yes) {
                                await Remove(version);
                                found = true;
                                break;
                            } else {
                                return;
                            }
                        }
                    }
                    if (!found) {
                        MessageBox.Show("导入的目标路径已经存在，并且不包含启动器已知的 Minecraft 安装。为了避免数据丢失，导入被中止。请手动删除这些文件。", "错误");
                        return;
                    }
                }

                var versionEntry = _versions.AddEntry(openFileDlg.SafeFileName, directory);
                versionEntry.StateChangeInfo = new VersionStateChangeInfo(VersionState.Extracting);
                await Task.Run(() => {
                    try {
                        ZipFile.ExtractToDirectory(openFileDlg.FileName, directory);
                    } catch (InvalidDataException ex) {
                        Debug.WriteLine("无法解压 APPX " + openFileDlg.FileName + ": " + ex.ToString());
                        MessageBox.Show("没有成功导入 APPX " + openFileDlg.SafeFileName + "。 它可能已经损坏或不是一个 APPX 文件。\n\n解压错误: " + ex.Message, "导入失败");
                        return;
                    } finally {
                        versionEntry.StateChangeInfo = null;
                    }
                });
            }
        }

        public ICommand LaunchCommand => new RelayCommand((v) => InvokeLaunch((Version)v));

        public ICommand RemoveCommand => new RelayCommand((v) => InvokeRemove((Version)v));

        public ICommand DownloadCommand => new RelayCommand((v) => InvokeDownload((Version)v));

        private void InvokeLaunch(Version v) {
            if (_hasLaunchTask)
                return;
            _hasLaunchTask = true;
            Task.Run(async () => {
                v.StateChangeInfo = new VersionStateChangeInfo(VersionState.Registering);
                string gameDir = Path.GetFullPath(v.GameDirectory);
                try {
                    await ReRegisterPackage(v.GamePackageFamily, gameDir);
                } catch (Exception e) {
                    Debug.WriteLine("应用重新注册失败：\n" + e.ToString());
                    MessageBox.Show("应用重新注册失败：\n" + e.ToString());
                    _hasLaunchTask = false;
                    v.StateChangeInfo = null;
                    return;
                }
                v.StateChangeInfo = new VersionStateChangeInfo(VersionState.Launching);
                try {
                    var pkg = await AppDiagnosticInfo.RequestInfoForPackageAsync(v.GamePackageFamily);
                    if (pkg.Count > 0)
                        await pkg[0].LaunchAsync();
                    Debug.WriteLine("应用完成启动！");
                    _hasLaunchTask = false;
                    v.StateChangeInfo = null;
                } catch (Exception e) {
                    Debug.WriteLine("应用启动失败：\n" + e.ToString());
                    MessageBox.Show("应用启动失败：\n" + e.ToString());
                    _hasLaunchTask = false;
                    v.StateChangeInfo = null;
                    return;
                }
            });
        }

        private async Task DeploymentProgressWrapper(IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> t) {
            TaskCompletionSource<int> src = new TaskCompletionSource<int>();
            t.Progress += (v, p) => {
                Debug.WriteLine("部署进度：" + p.state + " " + p.percentage + "%");
            };
            t.Completed += (v, p) => {
                if (p == AsyncStatus.Error) {
                    Debug.WriteLine("部署失败：" + v.GetResults().ErrorText);
                    src.SetException(new Exception("部署失败：" + v.GetResults().ErrorText));
                } else {
                    Debug.WriteLine("部署完成：" + p);
                    src.SetResult(1);
                }
            };
            await src.Task;
        }

        private string GetBackupMinecraftDataDir() {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string tmpDir = Path.Combine(localAppData, "TmpMinecraftLocalState");
            return tmpDir;
        }

        private void BackupMinecraftDataForRemoval(string packageFamily) {
            var data = ApplicationDataManager.CreateForPackageFamily(packageFamily);
            string tmpDir = GetBackupMinecraftDataDir();
            if (Directory.Exists(tmpDir)) {
                Debug.WriteLine("BackupMinecraftDataForRemoval错误: " + tmpDir + " 已经存在");
                Process.Start("explorer.exe", tmpDir);
                MessageBox.Show("用于备份 MC 数据的临时目录已经存在。这可能意味着我们上次备份数据时失败了。请手动备份该目录。");
                throw new Exception("临时目录存在");
            }
            Debug.WriteLine("将 Minecraft 数据移至 " + tmpDir);
            Directory.Move(data.LocalFolder.Path, tmpDir);
        }

        private void RestoreMove(string from, string to) {
            foreach (var f in Directory.EnumerateFiles(from)) {
                string ft = Path.Combine(to, Path.GetFileName(f));
                if (File.Exists(ft)) {
                    if (MessageBox.Show("文件 " + ft + " 已经存在于目标。\n你想替换它吗？否则旧文件会丢失。", "从以前的安装中恢复数据目录", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                        continue;
                    File.Delete(ft);
                }
                File.Move(f, ft);
            }
            foreach (var f in Directory.EnumerateDirectories(from)) {
                string tp = Path.Combine(to, Path.GetFileName(f));
                if (!Directory.Exists(tp)) {
                    if (File.Exists(tp) && MessageBox.Show("文件 " + tp + " 不是一个目录。\n你想替换它吗？否则旧文件会丢失。", "从以前的安装中恢复数据目录", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                        continue;
                    Directory.CreateDirectory(tp);
                }
                RestoreMove(f, tp);
            }
        }

        private void RestoreMinecraftDataFromReinstall(string packageFamily) {
            string tmpDir = GetBackupMinecraftDataDir();
            if (!Directory.Exists(tmpDir))
                return;
            var data = ApplicationDataManager.CreateForPackageFamily(packageFamily);
            Debug.WriteLine("将 Minecraft 备份数据移至：" + data.LocalFolder.Path);
            RestoreMove(tmpDir, data.LocalFolder.Path);
            Directory.Delete(tmpDir, true);
        }

        private async Task RemovePackage(Package pkg, string packageFamily) {
            Debug.WriteLine("移除包: " + pkg.Id.FullName);
            if (!pkg.IsDevelopmentMode) {
                BackupMinecraftDataForRemoval(packageFamily);
                await DeploymentProgressWrapper(new PackageManager().RemovePackageAsync(pkg.Id.FullName, 0));
            } else {
                Debug.WriteLine("包正在部署模式");
                await DeploymentProgressWrapper(new PackageManager().RemovePackageAsync(pkg.Id.FullName, RemovalOptions.PreserveApplicationData));
            }
            Debug.WriteLine("已经完成了包的移除：" + pkg.Id.FullName);
        }

        private string GetPackagePath(Package pkg) {
            try {
                return pkg.InstalledLocation.Path;
            } catch (FileNotFoundException) {
                return "";
            }
        }

        private async Task UnregisterPackage(string packageFamily, string gameDir) {
            foreach (var pkg in new PackageManager().FindPackages(packageFamily)) {
                string location = GetPackagePath(pkg);
                if (location == "" || location == gameDir) {
                    await RemovePackage(pkg, packageFamily);
                }
            }
        }

        private async Task ReRegisterPackage(string packageFamily, string gameDir) {
            foreach (var pkg in new PackageManager().FindPackages(packageFamily)) {
                string location = GetPackagePath(pkg);
                if (location == gameDir) {
                    Debug.WriteLine("跳过软件包的移除--同一路径：" + pkg.Id.FullName + " " + location);
                    return;
                }
                await RemovePackage(pkg, packageFamily);
            }
            Debug.WriteLine("注册包");
            string manifestPath = Path.Combine(gameDir, "AppxManifest.xml");
            await DeploymentProgressWrapper(new PackageManager().RegisterPackageAsync(new Uri(manifestPath), null, DeploymentOptions.DevelopmentMode));
            Debug.WriteLine("应用重新注册完成！");
            RestoreMinecraftDataFromReinstall(packageFamily);
        }

        private void InvokeDownload(Version v) {
            CancellationTokenSource cancelSource = new CancellationTokenSource();
            v.IsNew = false;
            v.StateChangeInfo = new VersionStateChangeInfo(VersionState.Initializing);
            v.StateChangeInfo.CancelCommand = new RelayCommand((o) => cancelSource.Cancel());

            Debug.WriteLine("开始下载");
            Task.Run(async () => {
                string dlPath = (v.VersionType == VersionType.Preview ? "Minecraft-Preview-" : "Minecraft-") + v.Name + ".Appx";
                VersionDownloader downloader = _anonVersionDownloader;
                if (v.VersionType == VersionType.Beta) {
                    downloader = _userVersionDownloader;
                    if (Interlocked.CompareExchange(ref _userVersionDownloaderLoginTaskStarted, 1, 0) == 0) {
                        _userVersionDownloaderLoginTask.Start();
                    }
                    Debug.WriteLine("等待验证");
                    try {
                        await _userVersionDownloaderLoginTask;
                        Debug.WriteLine("验证成功");
                    } catch (WUTokenHelper.WUTokenException e) {
                        Debug.WriteLine("验证失败：\n" + e.ToString());
                        MessageBox.Show("验证失败，因为：" + e.Message, "验证失败");
                        v.StateChangeInfo = null;
                        return;
                    } catch (Exception e) {
                        Debug.WriteLine("无法验证：\n" + e.ToString());
                        MessageBox.Show(e.ToString(), "无法验证");
                        v.StateChangeInfo = null;
                        return;
                    }
                }
                try {
                    await downloader.Download(v.UUID, "1", dlPath, (current, total) => {
                        if (v.StateChangeInfo.VersionState != VersionState.Downloading) {
                            Debug.WriteLine("实际下载开始");
                            v.StateChangeInfo.VersionState = VersionState.Downloading;
                            if (total.HasValue)
                                v.StateChangeInfo.TotalSize = total.Value;
                        }
                        v.StateChangeInfo.DownloadedBytes = current;
                    }, cancelSource.Token);
                    Debug.WriteLine("下载完成");
                } catch (BadUpdateIdentityException) {
                    Debug.WriteLine("下载因无法获取链接而失败");
                    MessageBox.Show(
                        "无法获取该版本的下载链接。" +
                        (v.VersionType == VersionType.Beta ? "\n对于测试版，请确保你的账户在 Xbox Insider Hub 应用程序中订阅了 Minecraft 测试计划。" : "")
                    );
                    v.StateChangeInfo = null;
                    return;
                } catch (Exception e) {
                    Debug.WriteLine("无法下载：\n" + e.ToString());
                    if (!(e is TaskCanceledException))
                        MessageBox.Show("无法下载：\n" + e.ToString());
                    v.StateChangeInfo = null;
                    return;
                }
                try {
                    v.StateChangeInfo.VersionState = VersionState.Extracting;
                    string dirPath = v.GameDirectory;
                    if (Directory.Exists(dirPath))
                        Directory.Delete(dirPath, true);
                    ZipFile.ExtractToDirectory(dlPath, dirPath);
                    v.StateChangeInfo = null;
                    File.Delete(Path.Combine(dirPath, "AppxSignature.p7x"));
                    if (UserPrefs.DeleteAppxAfterDownload) {
                        Debug.WriteLine("删除APPX以减少磁盘的占用");
                        File.Delete(dlPath);
                    } else {
                        Debug.WriteLine("由于用户的偏好而不能删除 APPX");
                    }
                } catch (Exception e) {
                    Debug.WriteLine("无法解压:\n" + e.ToString());
                    MessageBox.Show("无法解压:\n" + e.ToString());
                    v.StateChangeInfo = null;
                    return;
                }
                v.StateChangeInfo = null;
                v.UpdateInstallStatus();
            });
        }

        private async Task Remove(Version v) {
            v.StateChangeInfo = new VersionStateChangeInfo(VersionState.Uninstalling);
            await UnregisterPackage(v.GamePackageFamily, Path.GetFullPath(v.GameDirectory));
            Directory.Delete(v.GameDirectory, true);
            v.StateChangeInfo = null;
            if (v.IsImported) {
                Dispatcher.Invoke(() => _versions.Remove(v));
                Debug.WriteLine("删除导入的版本 " + v.DisplayName);
            } else {
                v.UpdateInstallStatus();
                Debug.WriteLine("删除正式版本 " + v.DisplayName);
            }
        }

        private void InvokeRemove(Version v) {
            Task.Run(async () => await Remove(v));
        }

        private void ShowInstalledVersionsOnlyCheckbox_Changed(object sender, RoutedEventArgs e) {
            UserPrefs.ShowInstalledOnly = ShowInstalledVersionsOnlyCheckbox.IsChecked ?? false;
            RefreshLists();
            RewritePrefs();
        }

        private void RefreshLists() {
            Dispatcher.Invoke(() => {
                foreach (var list in _versionListViews) {
                    list.View.Refresh();
                }
            });
        }

        private void DeleteAppxAfterDownloadCheck_Changed(object sender, RoutedEventArgs e) {
            UserPrefs.DeleteAppxAfterDownload = DeleteAppxAfterDownloadOption.IsChecked;
        }

        private void RewritePrefs() {
            File.WriteAllText(PREFS_PATH, JsonConvert.SerializeObject(UserPrefs));
        }

        private void MenuItemOpenLogFileClicked(object sender, RoutedEventArgs e) {
            if (!File.Exists(@"Log.txt")) {
                MessageBox.Show("找不到日志文件", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            } else 
                Process.Start(@"Log.txt");
        }

        private void MenuItemOpenDataDirClicked(object sender, RoutedEventArgs e) {
            Process.Start(@"explorer.exe", Directory.GetCurrentDirectory());
        }

        private void MenuItemCleanupForMicrosoftStoreReinstallClicked(object sender, RoutedEventArgs e) {
            var result = MessageBox.Show(
                "由启动器安装的 Minecraft 版本将被卸载。\n" +
                    "这将允许你从微软商店重新安装Minecraft。你的数据（世界等）将不会被删除。\n\n" +
                    "你确定想要继续？",
                "卸载所有版本",
                MessageBoxButton.OKCancel
            );
            if (result == MessageBoxResult.OK) {
                Debug.WriteLine("开始卸载所有版本！");
                foreach (var version in _versions) {
                    if (version.IsInstalled) {
                        InvokeRemove(version);
                    }
                }
                Debug.WriteLine("所有版本的预定卸载。");
            }
        }

        private void MenuItemRefreshVersionListClicked(object sender, RoutedEventArgs e) {
            Dispatcher.Invoke(LoadVersionList);
        }

        private void MenuItemAboutClicked(object sender, RoutedEventArgs e) {
            MessageBox.Show("Minecraft 基岩版版本管理器\n" +
                "版本：0.4.0\n" +
                "原作者：MCMrARM\n" +
                "发布者：AuroraStudio\n" +
                "使用到了 FastGit 作为版本信息传递源\n" +
                "WPF 主题：HandyControl\n" +
                "本项目使用 GPL 协议开源", "关于");
        }
    }

    struct MinecraftPackageFamilies
    {
        public static readonly string MINECRAFT = "Microsoft.MinecraftUWP_8wekyb3d8bbwe";
        public static readonly string MINECRAFT_PREVIEW = "Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe";
    }

    namespace WPFDataTypes {


        public class NotifyPropertyChangedBase : INotifyPropertyChanged {

            public event PropertyChangedEventHandler PropertyChanged;

            protected void OnPropertyChanged(string name) {
                if (PropertyChanged != null)
                    PropertyChanged(this, new PropertyChangedEventArgs(name));
            }

        }

        public interface ICommonVersionCommands {

            ICommand LaunchCommand { get; }

            ICommand DownloadCommand { get; }

            ICommand RemoveCommand { get; }

        }

        public enum VersionType : int
        {
            Release = 0,
            Beta = 1,
            Preview = 2,
            Imported = 100
        }

        public class Version : NotifyPropertyChangedBase {
            public static readonly string UNKNOWN_UUID = "未知";

            public Version(string uuid, string name, VersionType versionType, bool isNew, ICommonVersionCommands commands) {
                this.UUID = uuid;
                this.Name = name;
                this.VersionType = versionType;
                this.IsNew = isNew;
                this.DownloadCommand = commands.DownloadCommand;
                this.LaunchCommand = commands.LaunchCommand;
                this.RemoveCommand = commands.RemoveCommand;
                this.GameDirectory = (versionType == VersionType.Preview ? "Minecraft-Preview-" : "Minecraft-") + Name;
            }
            public Version(string name, string directory, ICommonVersionCommands commands) {
                this.UUID = UNKNOWN_UUID;
                this.Name = name;
                this.VersionType = VersionType.Imported;
                this.DownloadCommand = commands.DownloadCommand;
                this.LaunchCommand = commands.LaunchCommand;
                this.RemoveCommand = commands.RemoveCommand;
                this.GameDirectory = directory;
            }

            public string UUID { get; set; }
            public string Name { get; set; }
            public VersionType VersionType { get; set; }
            public bool IsNew {
                get { return _isNew; }
                set {
                    _isNew = value;
                    OnPropertyChanged("IsNew");
                }
            }
            public bool IsImported {
                get => VersionType == VersionType.Imported;
            }

            public string GameDirectory { get; set; }

            public string GamePackageFamily
            {
                get => VersionType == VersionType.Preview ? MinecraftPackageFamilies.MINECRAFT_PREVIEW : MinecraftPackageFamilies.MINECRAFT;
            }

            public bool IsInstalled => Directory.Exists(GameDirectory);

            public string DisplayName {
                get {
                    string typeTag = "";
                    if (VersionType == VersionType.Beta)
                        typeTag = "(测试版)";
                    else if (VersionType == VersionType.Preview)
                        typeTag = "(预览版)";
                    return Name + (typeTag.Length > 0 ? " " + typeTag : "") + (IsNew ? " (新版！)" : "");
                }
            }
            public string DisplayInstallStatus {
                get {
                    return IsInstalled ? "已安装" : "未安装";
                }
            }

            public ICommand LaunchCommand { get; set; }
            public ICommand DownloadCommand { get; set; }
            public ICommand RemoveCommand { get; set; }

            private VersionStateChangeInfo _stateChangeInfo;
            private bool _isNew = false;
            public VersionStateChangeInfo StateChangeInfo {
                get { return _stateChangeInfo; }
                set { _stateChangeInfo = value; OnPropertyChanged("StateChangeInfo"); OnPropertyChanged("IsStateChanging"); }
            }

            public bool IsStateChanging => StateChangeInfo != null;

            public void UpdateInstallStatus() {
                OnPropertyChanged("IsInstalled");
            }

        }

        public enum VersionState {
            Initializing,
            Downloading,
            Extracting,
            Registering,
            Launching,
            Uninstalling
        };

        public class VersionStateChangeInfo : NotifyPropertyChangedBase {

            private VersionState _versionState;

            private long _downloadedBytes;
            private long _totalSize;

            public VersionStateChangeInfo(VersionState versionState) {
                _versionState = versionState;
            }

            public VersionState VersionState {
                get { return _versionState; }
                set {
                    _versionState = value;
                    OnPropertyChanged("IsProgressIndeterminate");
                    OnPropertyChanged("DisplayStatus");
                }
            }

            public bool IsProgressIndeterminate {
                get {
                    switch (_versionState) {
                        case VersionState.Initializing:
                        case VersionState.Extracting:
                        case VersionState.Uninstalling:
                        case VersionState.Registering:
                        case VersionState.Launching:
                            return true;
                        default: return false;
                    }
                }
            }

            public long DownloadedBytes {
                get { return _downloadedBytes; }
                set { _downloadedBytes = value; OnPropertyChanged("DownloadedBytes"); OnPropertyChanged("DisplayStatus"); }
            }

            public long TotalSize {
                get { return _totalSize; }
                set { _totalSize = value; OnPropertyChanged("TotalSize"); OnPropertyChanged("DisplayStatus"); }
            }

            public string DisplayStatus {
                get {
                    switch (_versionState) {
                        case VersionState.Initializing: return "准备中...";
                        case VersionState.Downloading:
                            return "下载中... " + (DownloadedBytes / 1024 / 1024) + "MiB/" + (TotalSize / 1024 / 1024) + "MiB";
                        case VersionState.Extracting: return "解压中...";
                        case VersionState.Registering: return "正在注册包...";
                        case VersionState.Launching: return "启动中...";
                        case VersionState.Uninstalling: return "卸载中...";
                        default: return "Wtf is happening? ...";
                    }
                }
            }

            public ICommand CancelCommand { get; set; }

        }

    }
}
