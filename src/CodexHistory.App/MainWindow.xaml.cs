using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexHistory.Core;
using CodexHistory.Infrastructure;
using Microsoft.Win32;

namespace CodexHistory.App;

public partial class MainWindow : System.Windows.Window
{
    private const int ContentPreviewLimit = 64 * 1024;
    private readonly ObservableCollection<SessionRow> visibleSessions = [];
    private IReadOnlyList<SessionSummary> sessions = [];
    private CancellationTokenSource? scanCancellation;
    private int detailLoadVersion;
    private IHistoryCatalog? catalog;
    private string sourcePath;
    private string databasePath;
    private readonly string primaryDatabasePath;
    private string? temporaryWorkspace;
    private IReadOnlyCollection<SessionSummary> filteredSessions = [];
    private bool darkTheme;

    private static readonly IReadOnlyDictionary<string, string> LightPalette = new Dictionary<string, string>
    {
        ["CanvasBrush"] = "#F1F0EC", ["SurfaceBrush"] = "#FAF9F5",
        ["SurfaceRaisedBrush"] = "#E7E6E1", ["BorderBrush"] = "#B9B8B2",
        ["TextBrush"] = "#171717", ["MutedBrush"] = "#686762",
        ["SelectionBrush"] = "#FFE0DE", ["InverseBrush"] = "#111111",
        ["InverseTextBrush"] = "#F7F6F1", ["SidebarMutedBrush"] = "#B8B8B8",
        ["SidebarRuleBrush"] = "#3E3E3E"
    };

    private static readonly IReadOnlyDictionary<string, string> DarkPalette = new Dictionary<string, string>
    {
        ["CanvasBrush"] = "#111318", ["SurfaceBrush"] = "#191C22",
        ["SurfaceRaisedBrush"] = "#232730", ["BorderBrush"] = "#3A404C",
        ["TextBrush"] = "#F6F3EE", ["MutedBrush"] = "#AAAEB8",
        ["SelectionBrush"] = "#3B2428", ["InverseBrush"] = "#F7F6F1",
        ["InverseTextBrush"] = "#111111", ["SidebarMutedBrush"] = "#575757",
        ["SidebarRuleBrush"] = "#C9C9C9"
    };

    public MainWindow(string? sourcePath = null, string? databasePath = null)
    {
        InitializeComponent();
        this.sourcePath = Path.GetFullPath(sourcePath ?? DefaultSourcePath());
        this.databasePath = Path.GetFullPath(databasePath ?? DefaultDatabasePath());
        primaryDatabasePath = this.databasePath;
        SessionsList.ItemsSource = visibleSessions;
        CollectionViewSource.GetDefaultView(visibleSessions).GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(SessionRow.DateGroup)));
        UpdateSourceDisplay();
        StatusText.Text = "Kaynak hazır · Taramayı başlatınca oturumlar gösterilir.";
        WaterScenarioFilter.AddHandler(TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(WaterScenarioTextChanged));
        FitToWorkingArea();
        Loaded += (_, _) => FitToWorkingArea();
    }

    private static string DefaultSourcePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    private static string DefaultDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexHistory", "index.db");

    private void SelectSource_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (scanCancellation is not null) return;
        var dialog = new OpenFolderDialog
        {
            Title = "Codex oturum kaynağını seçin",
            InitialDirectory = Directory.Exists(sourcePath) ? sourcePath : null
        };

        if (dialog.ShowDialog(this) != true) return;
        if (temporaryWorkspace is not null)
        {
            CleanupTemporaryWorkspace();
            databasePath = primaryDatabasePath;
            catalog = null;
        }
        sourcePath = Path.GetFullPath(dialog.FolderName);
        sessions = [];
        PopulateFilters();
        ApplyFilters();
        UpdateSourceDisplay();
        StatusText.Text = "Yeni kaynak seçildi · Taramayı başlatın.";
    }

    private async void Demo_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (scanCancellation is not null) return;
        try
        {
            ConfigureDemoWorkspace();
            await ScanAsync();
            StatusText.Text = $"Sentetik demo hazır · {sessions.Count:N0} oturum";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Demo hazırlanamadı: {exception.Message}";
        }
    }

    private async void Scan_Click(object sender, System.Windows.RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (scanCancellation is not null) return;
        if (!Directory.Exists(sourcePath))
        {
            StatusText.Text = "Kaynak klasörü bulunamadı.";
            return;
        }

        var cancellation = new CancellationTokenSource();
        scanCancellation = cancellation;
        var scanSourcePath = sourcePath;
        var scanDatabasePath = databasePath;
        SetBusy(true);
        StatusText.Text = "Taranıyor…";
        await Dispatcher.Yield(DispatcherPriority.Render);
        try
        {
            catalog ??= new SqliteHistoryCatalog(scanDatabasePath);
            var activeCatalog = catalog;
            var result = await Task.Run(
                () => activeCatalog.RefreshAsync(scanSourcePath, cancellation.Token),
                cancellation.Token);
            sessions = await activeCatalog.ListSessionsAsync(cancellation.Token);
            PopulateFilters();
            ApplyFilters();
            var locked = result.LockedFilesSkipped == 0
                ? string.Empty
                : $" · {result.LockedFilesSkipped:N0} kilitli dosya atlandı";
            StatusText.Text = result.RefreshDeferred
                ? "Kilitli kaynak nedeniyle yenileme ertelendi; önceki indeks gösteriliyor."
                : $"{result.FilesScanned:N0} dosya tarandı · {result.FilesIndexed:N0} güncellendi · " +
                  $"{result.MalformedLines:N0} bozuk satır atlandı{locked}";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Tarama iptal edildi.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Tarama başarısız: {exception.Message}";
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(scanCancellation, cancellation))
            {
                scanCancellation = null;
                SetBusy(false);
            }
        }
    }

    private void Cancel_Click(object sender, System.Windows.RoutedEventArgs e) => scanCancellation?.Cancel();

    private async void ImportPrices_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (scanCancellation is not null) return;
        var dialog = new OpenFileDialog { Filter = "CSV dosyası|*.csv" };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            catalog ??= new SqliteHistoryCatalog(databasePath);
            var result = await catalog.ImportPricesAsync(dialog.FileName);
            StatusText.Text =
                $"{result.ImportedRows:N0} fiyat satırı içe aktarıldı, {result.RejectedRows:N0} reddedildi.";
            if (sessions.Count > 0) await ScanAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Fiyatlar içe aktarılamadı: {exception.Message}";
        }
    }

    private void PopulateFilters()
    {
        FillFilter(ModelFilter, "Tüm modeller", sessions.Select(session => session.Model), SelectedTag(ModelFilter));
        FillFilter(ProjectFilter, "Tüm projeler", sessions.Select(session => session.Project), SelectedTag(ProjectFilter));
    }

    private static void FillFilter(
        ComboBox filter, string allLabel, IEnumerable<string?> values, string previousSelection)
    {
        filter.Items.Clear();
        filter.Items.Add(new ComboBoxItem { Content = allLabel, Tag = string.Empty });
        foreach (var value in values
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.CurrentCultureIgnoreCase))
        {
            filter.Items.Add(new ComboBoxItem { Content = value, Tag = value });
        }

        filter.SelectedItem = filter.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, previousSelection, StringComparison.OrdinalIgnoreCase));
        if (filter.SelectedIndex < 0) filter.SelectedIndex = 0;
    }

    private void FilterChanged(object sender, EventArgs e)
    {
        if (IsInitialized) ApplyFilters();
    }

    private void ApplyFilters()
    {
        var selectedId = (SessionsList.SelectedItem as SessionRow)?.Summary.Id;
        var search = SearchBox?.Text?.Trim() ?? string.Empty;
        var model = SelectedTag(ModelFilter);
        var project = SelectedTag(ProjectFilter);
        var earliest = DateFilter?.SelectedIndex switch
        {
            1 => DateTimeOffset.Now.AddDays(-7),
            2 => DateTimeOffset.Now.AddDays(-30),
            _ => DateTimeOffset.MinValue
        };

        var filtered = sessions
            .Where(session => MatchesSearch(session, search))
            .Where(session => model.Length == 0 || string.Equals(session.Model, model, StringComparison.OrdinalIgnoreCase))
            .Where(session => project.Length == 0 || string.Equals(session.Project, project, StringComparison.OrdinalIgnoreCase))
            .Where(session => session.UpdatedAt >= earliest)
            .ToArray();
        filteredSessions = filtered;

        visibleSessions.Clear();
        foreach (var session in filtered) visibleSessions.Add(new SessionRow(session));
        UpdateSummaryCards(filtered);

        var selection = visibleSessions.FirstOrDefault(item => item.Summary.Id == selectedId)
            ?? visibleSessions.FirstOrDefault();
        SessionsList.SelectedItem = selection;
        if (selection is not null)
            SessionsList.ScrollIntoView(selection);
        else
            ResetDetailPanel(noResults: true);
    }

    private static string SelectedTag(ComboBox? filter) =>
        (filter?.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

    private static bool MatchesSearch(SessionSummary session, string search) =>
        search.Length == 0
        || (session.Title?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false)
        || (session.Project?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false)
        || session.Id.Contains(search, StringComparison.OrdinalIgnoreCase);

    private void UpdateSummaryCards(IReadOnlyCollection<SessionSummary> filtered)
    {
        ResultCountText.Text = $"{filtered.Count:N0} sonuç";
        SessionCountText.Text = $"{filtered.Count:N0}";
        TokenTotalText.Text =
            $"Girdi {filtered.Sum(item => item.InputTokens):N0}\n" +
            $"Önbellek {filtered.Sum(item => item.CachedInputTokens):N0}\n" +
            $"Çıktı {filtered.Sum(item => item.OutputTokens):N0}";

        UpdateWaterEstimate();

        var priced = filtered.Where(item => item.EstimatedCost.HasValue).ToArray();
        CostText.Text = priced.Length == 0
            ? "Bilinmiyor"
            : $"${priced.Sum(item => item.EstimatedCost!.Value):N4}" +
              (priced.Length != filtered.Count || priced.Any(item => item.CostIsPartial) ? " · kısmi" : string.Empty);
        LatestText.Text = filtered.Count == 0
            ? "—"
            : filtered.Max(item => item.UpdatedAt).ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.CurrentCulture);
    }

    private void WaterScenarioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) UpdateWaterEstimate();
    }

    private void WaterScenarioTextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) UpdateWaterEstimate();
    }

    private void UpdateWaterEstimate()
    {
        if (WaterEstimateText is null) return;
        var coefficientText = WaterScenarioFilter?.Text?.Trim() ?? string.Empty;
        var unitIndex = coefficientText.IndexOf("mL", StringComparison.OrdinalIgnoreCase);
        var validUnit = true;
        if (unitIndex >= 0)
        {
            var suffix = coefficientText[unitIndex..].Replace(" ", string.Empty);
            validUnit = suffix.Equals("mL", StringComparison.OrdinalIgnoreCase)
                        || suffix.Equals("mL/1k", StringComparison.OrdinalIgnoreCase);
            coefficientText = coefficientText[..unitIndex].Trim();
        }
        decimal millilitersPerThousandTokens = 0;
        var parsed = validUnit && (decimal.TryParse(coefficientText, NumberStyles.Float, CultureInfo.CurrentCulture,
                         out millilitersPerThousandTokens)
                     || decimal.TryParse(coefficientText.Replace(',', '.'), NumberStyles.Float,
                         CultureInfo.InvariantCulture, out millilitersPerThousandTokens));
        if (!parsed || millilitersPerThousandTokens < 0 || millilitersPerThousandTokens > 1_000_000m)
        {
            WaterEstimateText.Text = "Geçerli katsayı girin";
            return;
        }

        try
        {
            var milliliters = filteredSessions.Aggregate(0m, (total, item) =>
                checked(total + WaterFootprintEstimator.Estimate(item, millilitersPerThousandTokens)));
            var uncertain = filteredSessions.Any(item => item.UsageIsUncertain) ? " · kısmi" : string.Empty;
            WaterEstimateText.Text = $"≈ {milliliters:N1} mL{uncertain}";
        }
        catch (OverflowException)
        {
            WaterEstimateText.Text = "Katsayı çok büyük";
        }
    }

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        darkTheme = !darkTheme;
        foreach (var (key, color) in darkTheme ? DarkPalette : LightPalette) SetBrush(key, color);
        ThemeButton.Content = darkTheme ? "Açık tema" : "Koyu tema";
    }

    private static void SetBrush(string key, string color) =>
        Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    private void FitToWorkingArea()
    {
        var area = SystemParameters.WorkArea;
        const double margin = 16;
        var availableWidth = Math.Max(1, area.Width - margin * 2);
        var availableHeight = Math.Max(1, area.Height - margin * 2);

        WindowStartupLocation = WindowStartupLocation.Manual;
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
        Left = area.Left + Math.Max(margin, (area.Width - Width) / 2);
        Top = area.Top + Math.Max(margin, (area.Height - Height) / 2);
    }

    private async void SessionSelected(object sender, SelectionChangedEventArgs e)
    {
        var loadVersion = ++detailLoadVersion;
        ContentPreview.Text = "İçerik varsayılan olarak okunmaz.";
        ContentButton.IsEnabled = false;
        if (SessionsList.SelectedItem is not SessionRow row || catalog is null)
        {
            if (visibleSessions.Count == 0) ResetDetailPanel(noResults: true);
            return;
        }

        DetailTitle.Text = row.DisplayTitle;
        DetailMeta.Text = "Ayrıntılar yükleniyor…";
        TimelineList.ItemsSource = new[] { "Zaman çizelgesi yükleniyor…" };
        QuotaList.ItemsSource = new[] { "Kota anlık görüntüleri yükleniyor…" };

        try
        {
            var detail = await catalog.GetSessionAsync(row.Summary.Id);
            if (loadVersion != detailLoadVersion) return;
            if (detail is null)
            {
                DetailMeta.Text = string.Empty;
                DetailSummary.Text = "Oturum ayrıntısı bulunamadı. Listeyi yeniden taramayı deneyin.";
                TimelineList.ItemsSource = new[] { "Bu oturum için zaman çizelgesi bulunamadı." };
                QuotaList.ItemsSource = new[] { "Bu oturum için kota anlık görüntüsü bulunamadı." };
                return;
            }

            var summary = detail.Summary;
            DetailTitle.Text = row.DisplayTitle;
            DetailMeta.Text =
                $"{Display(summary.Model)} · {Display(summary.Project)} · {summary.UpdatedAt.ToLocalTime():g}";
            DetailSummary.Text = BuildDetailSummary(summary);
            TimelineList.ItemsSource = detail.Timeline.Count == 0
                ? new[] { "Bu oturumda kullanım veya araç olayı bulunamadı." }
                : detail.Timeline.Select(FormatTimelineEvent).ToArray();
            QuotaList.ItemsSource = detail.QuotaSnapshots.Count == 0
                ? new[] { "Bu oturumun günlüğünde kota anlık görüntüsü yok." }
                : detail.QuotaSnapshots.Select(FormatQuotaSnapshot).ToArray();
            ContentPreview.Text = "İçerik varsayılan olarak okunmaz. Görmek için “İçeriği geçici göster” düğmesini kullanın.";
            ContentButton.IsEnabled = CanPreviewSource(summary.SourcePath);
        }
        catch (Exception exception)
        {
            if (loadVersion != detailLoadVersion) return;
            DetailMeta.Text = string.Empty;
            DetailSummary.Text = $"Oturum ayrıntısı yüklenemedi: {exception.Message}";
            TimelineList.ItemsSource = new[] { "Zaman çizelgesi yüklenemedi." };
            QuotaList.ItemsSource = new[] { "Kota anlık görüntüleri yüklenemedi." };
            StatusText.Text = $"Ayrıntı yüklenemedi: {exception.Message}";
        }
    }

    private void ResetDetailPanel(bool noResults)
    {
        detailLoadVersion++;
        DetailTitle.Text = noResults ? "Gösterilecek oturum yok" : "Bir oturum seçin";
        DetailMeta.Text = string.Empty;
        DetailSummary.Text = noResults
            ? "Filtrelerle eşleşen bir oturum bulunamadı. Filtreleri değiştirin veya kaynağı yeniden tarayın."
            : "Metadata, araçlar ve kota anlık görüntüleri burada görünür.";
        TimelineList.ItemsSource = new[] { "Bir oturum seçildiğinde zaman çizelgesi burada görünür." };
        QuotaList.ItemsSource = new[] { "Bir oturum seçildiğinde kota anlık görüntüleri burada görünür." };
        ContentPreview.Text = "İçerik varsayılan olarak okunmaz.";
        ContentButton.IsEnabled = false;
    }

    private static string BuildDetailSummary(SessionSummary summary)
    {
        var uncertainty = summary.UsageIsUncertain ? " · kullanım kısmi/belirsiz" : string.Empty;
        var malformed = summary.MalformedLineCount > 0 ? $" · {summary.MalformedLineCount:N0} bozuk satır" : string.Empty;
        var cost = summary.EstimatedCost is null
            ? "bilinmiyor"
            : $"${summary.EstimatedCost:N4}" + (summary.CostIsPartial ? " (kısmi)" : string.Empty);
        var recordedInterval = summary.UpdatedAt >= summary.StartedAt
            ? FormatDuration(summary.UpdatedAt - summary.StartedAt)
            : "bilinmiyor";
        return
            $"Girdi {summary.InputTokens:N0} · Önbellek {summary.CachedInputTokens:N0} · " +
            $"Çıktı {summary.OutputTokens:N0} · Akıl yürütme {summary.ReasoningOutputTokens:N0}\n" +
            $"Kayıt aralığı {recordedInterval} · {summary.ToolCallCount:N0} araç çağrısı · " +
            $"Güncel standart API liste fiyatıyla tahmini karşılık {cost}" +
            uncertainty + malformed + "\nBu değer Codex abonelik faturası değildir.";
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours:N0} sa {duration.Minutes:N0} dk"
        : duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes:N0} dk {duration.Seconds:N0} sn"
            : $"{Math.Max(0, (int)duration.TotalSeconds):N0} sn";

    private static string FormatTimelineEvent(HistoryEvent item)
    {
        var usage = item.InputTokens.HasValue || item.OutputTokens.HasValue
            ? $" · G {item.InputTokens ?? 0:N0} · Önb {item.CachedInputTokens ?? 0:N0} · Ç {item.OutputTokens ?? 0:N0}"
            : string.Empty;
        var uncertainty = item.UsageIsUncertain ? " · kısmi/belirsiz" : string.Empty;
        return $"{item.Timestamp.ToLocalTime():dd MMM HH:mm:ss}   {Display(item.Kind)}   " +
               $"{Display(item.Name ?? item.Model)}{usage}{uncertainty}";
    }

    private static string FormatQuotaSnapshot(QuotaSnapshot snapshot)
    {
        var usage = snapshot.UsedPercent is null ? "oran bilinmiyor" : $"%{snapshot.UsedPercent:N0} kullanılmış";
        var reset = snapshot.ResetsAt is null
            ? "sıfırlanma bilinmiyor"
            : $"sıfırlanma {snapshot.ResetsAt.Value.ToLocalTime():g}";
        return $"{snapshot.Timestamp.ToLocalTime():dd MMM HH:mm}   {Display(snapshot.LimitName)}   {usage}   {reset}";
    }

    private async void ShowContent_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (SessionsList.SelectedItem is not SessionRow row || !CanPreviewSource(row.Summary.SourcePath))
        {
            ContentPreview.Text = "Bu kaynak için geçici içerik gösterimi kullanılamıyor.";
            return;
        }

        var selectionVersion = detailLoadVersion;
        try
        {
            var preview = await ReadContentPreviewAsync(row.Summary.SourcePath);
            if (selectionVersion == detailLoadVersion
                && (SessionsList.SelectedItem as SessionRow)?.Summary.Id == row.Summary.Id)
                ContentPreview.Text = preview;
        }
        catch (Exception exception)
        {
            if (selectionVersion == detailLoadVersion
                && (SessionsList.SelectedItem as SessionRow)?.Summary.Id == row.Summary.Id)
                ContentPreview.Text = $"İçerik okunamadı: {exception.Message}";
        }
    }

    private bool CanPreviewSource(string path)
    {
        if (!File.Exists(path) || Path.GetFileName(path).Equals("auth.json", StringComparison.OrdinalIgnoreCase)) return false;
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(sourcePath, fullPath);
        if (relative.Length == 0 || Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;

        try
        {
            // A lexical child may link outside the selected source or alias auth.json.
            for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static async Task<string> ReadContentPreviewAsync(string path)
    {
        var preview = new StringBuilder();
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);

        while (preview.Length < ContentPreviewLimit && await reader.ReadLineAsync() is { } line)
        {
            TryAppendContent(line, preview);
        }

        if (preview.Length == 0) return "Desteklenen konuşma içeriği bulunamadı.";
        var bounded = preview.ToString(0, Math.Min(preview.Length, ContentPreviewLimit));
        return preview.Length >= ContentPreviewLimit
            ? bounded + "\n… 65.536 karakter sınırı"
            : bounded;
    }

    private static void TryAppendContent(string line, StringBuilder preview)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object) return;

            var role = payload.TryGetProperty("role", out var roleValue)
                && roleValue.ValueKind == JsonValueKind.String
                ? roleValue.GetString()
                : null;
            if (payload.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    if (preview.Length >= ContentPreviewLimit) break;
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("text", out var textValue)
                        || textValue.ValueKind != JsonValueKind.String)
                        continue;
                    AppendPreviewText(preview, role, textValue.GetString());
                }
                return;
            }

            var text = payload.TryGetProperty("message", out var message)
                ? message.ToString()
                : payload.TryGetProperty("text", out var directText) ? directText.ToString() : string.Empty;
            AppendPreviewText(preview, role, text);
        }
        catch (JsonException)
        {
            // Aktif günlükteki yarım veya bozuk satır içerik önizlemesini durdurmaz.
        }
    }

    private static void AppendPreviewText(StringBuilder preview, string? role, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        AppendBounded(preview, string.IsNullOrWhiteSpace(role) ? string.Empty : $"[{role}]\n");
        AppendBounded(preview, text);
        AppendBounded(preview, "\n\n");
    }

    private static void AppendBounded(StringBuilder preview, string text)
    {
        var remaining = ContentPreviewLimit - preview.Length;
        if (remaining > 0) preview.Append(text, 0, Math.Min(text.Length, remaining));
    }

    internal async Task RunSmokeAsync(string pngPath)
    {
        var boundedPreview = new StringBuilder();
        TryAppendContent("[]", boundedPreview);
        TryAppendContent("{\"payload\":[]}", boundedPreview);
        AppendPreviewText(boundedPreview, "user", new string('x', ContentPreviewLimit + 1));
        if (boundedPreview.Length != ContentPreviewLimit)
            throw new InvalidOperationException("İçerik önizlemesinin karakter sınırı uygulanmadı.");

        ConfigureDemoWorkspace();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await ScanAsync();
        if (sessions.Count < 2) throw new InvalidOperationException("Demo oturumları yüklenmedi.");
        if (SessionsList.SelectedItem is null)
            throw new InvalidOperationException("İlk görünür oturum otomatik seçilmedi.");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (TimelineList.Items.Count == 0) throw new InvalidOperationException("Oturum ayrıntısı otomatik yüklenmedi.");
        if (!sessions.Any(session => session.EstimatedCost.HasValue))
            throw new InvalidOperationException("Varsayılan fiyat kataloğu demo maliyetini hesaplamadı.");
        var selected = (SessionRow)SessionsList.SelectedItem;
        var preview = await ReadContentPreviewAsync(selected.Summary.SourcePath);
        if (!preview.Contains("Sentetik demo mesajı", StringComparison.Ordinal))
            throw new InvalidOperationException("Geçici içerik önizlemesi gerçek mesaj şemasını okuyamadı.");

        ModelFilter.SelectedIndex = Math.Min(1, ModelFilter.Items.Count - 1);
        if (visibleSessions.Count == 0 || visibleSessions.Count >= sessions.Count)
        {
            throw new InvalidOperationException("Model filtresi beklenen sonucu vermedi.");
        }

        ModelFilter.SelectedIndex = 0;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (TimelineList.Items.Count == 0) throw new InvalidOperationException("Oturum ayrıntısı yüklenmedi.");
        if (QuotaList.Items.Count == 0) throw new InvalidOperationException("Kota sekmesi verisi yüklenmedi.");
        if (!WaterEstimateText.Text.StartsWith("≈", StringComparison.Ordinal))
            throw new InvalidOperationException("Su senaryosu hesaplanmadı.");

        WaterScenarioFilter.SelectedIndex = -1;
        WaterScenarioFilter.Text = "1,5";
        UpdateWaterEstimate();
        if (!WaterEstimateText.Text.StartsWith("≈ 5", StringComparison.Ordinal))
            throw new InvalidOperationException("Virgüllü özel su katsayısı uygulanmadı.");
        WaterScenarioFilter.Text = "geçersiz";
        UpdateWaterEstimate();
        if (WaterEstimateText.Text != "Geçerli katsayı girin")
            throw new InvalidOperationException("Geçersiz su katsayısı doğrulanmadı.");
        WaterScenarioFilter.Text = "1000001";
        UpdateWaterEstimate();
        if (WaterEstimateText.Text != "Geçerli katsayı girin")
            throw new InvalidOperationException("Aşırı büyük su katsayısı reddedilmedi.");
        WaterScenarioFilter.Text = "1.0";
        UpdateWaterEstimate();

        Width = 1200;
        Height = 760;
        UpdateLayout();
        var basePath = Path.GetFullPath(pngPath);
        SaveScreenshot(CompanionScreenshotPath(basePath, "light"));

        var initialLightColors = LightPalette.ToDictionary(
            item => item.Key, item => ((SolidColorBrush)Application.Current.Resources[item.Key]).Color);
        ToggleTheme_Click(this, new RoutedEventArgs());
        UpdateLayout();
        SaveScreenshot(CompanionScreenshotPath(basePath, "dark"));
        ToggleTheme_Click(this, new RoutedEventArgs());
        foreach (var (key, color) in initialLightColors)
        {
            if (((SolidColorBrush)Application.Current.Resources[key]).Color != color)
                throw new InvalidOperationException($"Açık tema rengi geri yüklenmedi: {key}");
        }

        Width = 1000;
        Height = 650;
        UpdateLayout();
        if (Math.Abs(ActualWidth - 1000) > 1 || Math.Abs(ActualHeight - 650) > 1)
            throw new InvalidOperationException($"1000×650 pencere boyutu uygulanmadı: {ActualWidth:N0}×{ActualHeight:N0}");
        if (ActualWidth > SystemParameters.WorkArea.Width || ActualHeight > SystemParameters.WorkArea.Height)
            throw new InvalidOperationException("Pencere küçük çalışma alanına sığmadı.");
        SaveScreenshot(CompanionScreenshotPath(basePath, "small"));

        if (!double.IsPositiveInfinity(MaxWidth) || !double.IsPositiveInfinity(MaxHeight))
            throw new InvalidOperationException("Pencere büyütme sınırı kalıcı olarak kısıtlandı.");
        WindowState = WindowState.Maximized;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (WindowState != WindowState.Maximized)
            throw new InvalidOperationException("Pencere büyütülemedi.");
        WindowState = WindowState.Normal;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (WindowState != WindowState.Normal)
            throw new InvalidOperationException("Pencere normal boyuta dönemedi.");

        SaveScreenshot(basePath);
        CleanupTemporaryWorkspace();
    }

    private static string CompanionScreenshotPath(string path, string label)
    {
        var directory = Path.GetDirectoryName(path)!;
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(path)}.{label}{Path.GetExtension(path)}");
    }

    private void ConfigureDemoWorkspace()
    {
        CleanupTemporaryWorkspace();
        temporaryWorkspace = Path.Combine(Path.GetTempPath(), "CodexHistory", Guid.NewGuid().ToString("N"));
        sourcePath = Path.Combine(temporaryWorkspace, "source");
        UpdateSourceDisplay("Sentetik demo");
        SourcePathText.Text = "Geçici örnek veriler";
        databasePath = Path.Combine(temporaryWorkspace, "database", "demo.db");
        Directory.CreateDirectory(Path.Combine(sourcePath, "sessions"));

        WriteDemoLog(Path.Combine(sourcePath, "sessions", "analysis.jsonl"), "demo-analysis",
            @"C:\Demo\Analiz", "gpt-5.6-sol", 1_840, 620, 410, 90, "search");
        WriteDemoLog(Path.Combine(sourcePath, "sessions", "ui.jsonl"), "demo-ui",
            @"C:\Demo\Arayuz", "gpt-5.6-luna", 960, 280, 230, 35, "apply_patch");

        catalog = null;
        sessions = [];
        visibleSessions.Clear();
        StatusText.Text = "Sentetik demo hazırlanıyor…";
    }

    private static void WriteDemoLog(
        string path, string id, string project, string model,
        long input, long cached, long output, long reasoning, string toolName)
    {
        var now = DateTimeOffset.UtcNow;
        var lines = new[]
        {
            JsonSerializer.Serialize(new
            {
                timestamp = now.AddMinutes(-4), type = "session_meta",
                payload = new { id, cwd = project, title = id.Replace('-', ' ') }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = now.AddMinutes(-3), type = "turn_context", payload = new { model }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = now.AddMinutes(-2.5), type = "response_item",
                payload = new
                {
                    type = "message",
                    role = "user",
                    content = new[] { new { type = "input_text", text = "Sentetik demo mesajı; indekse kaydedilmez." } }
                }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = now.AddMinutes(-2), type = "response_item",
                payload = new { type = "function_call", name = toolName, call_id = $"call-{id}" }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = now.AddMinutes(-1), type = "token_usage_record",
                payload = new
                {
                    response_id = $"response-{id}",
                    usage = new
                    {
                        input_tokens = input, cached_input_tokens = cached,
                        output_tokens = output, reasoning_output_tokens = reasoning
                    }
                }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = now, type = "event_msg",
                payload = new
                {
                    type = "token_count",
                    info = new
                    {
                        total_token_usage = new
                        {
                            input_tokens = input, cached_input_tokens = cached,
                            output_tokens = output, reasoning_output_tokens = reasoning
                        },
                        last_token_usage = new
                        {
                            input_tokens = input, cached_input_tokens = cached,
                            output_tokens = output, reasoning_output_tokens = reasoning
                        },
                        model_context_window = 200_000
                    },
                    rate_limits = new
                    {
                        limit_id = "codex-demo",
                        limit_name = "Sentetik kota",
                        primary = new
                        {
                            used_percent = 38,
                            window_minutes = 300,
                            resets_at = now.AddHours(2).ToUnixTimeSeconds()
                        },
                        secondary = new
                        {
                            used_percent = 57,
                            window_minutes = 10_080,
                            resets_at = now.AddDays(4).ToUnixTimeSeconds()
                        }
                    }
                }
            })
        };
        File.WriteAllLines(path, lines);
    }

    private void SaveScreenshot(string path)
    {
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(1, (int)Math.Ceiling(RootGrid.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(RootGrid.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(
            width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(RootGrid);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var output = File.Create(fullPath);
        encoder.Save(output);
    }

    private void CleanupTemporaryWorkspace()
    {
        if (temporaryWorkspace is null || !Directory.Exists(temporaryWorkspace)) return;
        try
        {
            Directory.Delete(temporaryWorkspace, recursive: true);
            temporaryWorkspace = null;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    protected override void OnClosed(EventArgs e)
    {
        scanCancellation?.Cancel();
        CleanupTemporaryWorkspace();
        base.OnClosed(e);
    }

    private void SetBusy(bool busy)
    {
        DemoButton.IsEnabled = !busy;
        SelectSourceButton.IsEnabled = !busy;
        ImportPricesButton.IsEnabled = !busy;
        ScanButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSourceDisplay(string? displayName = null)
    {
        SourceNameText.Text = displayName ?? new DirectoryInfo(sourcePath).Name;
        SourcePathText.Text = sourcePath;
    }

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "Bilinmiyor" : value;

    private sealed record SessionRow(SessionSummary Summary)
    {
        public string DisplayTitle => string.IsNullOrWhiteSpace(Summary.Title)
            ? $"Oturum {Summary.Id}"
            : Summary.Title;
        public string Subtitle =>
            $"{Display(Summary.Project)} · {Display(Summary.Model)} · " +
            $"{Summary.InputTokens + Summary.OutputTokens:N0} token" +
            (Summary.UsageIsUncertain ? " · kısmi" : string.Empty);
        public string Updated => Summary.UpdatedAt.ToLocalTime().ToString("dd MMM HH:mm");
        public string DateGroup
        {
            get
            {
                var localDate = Summary.UpdatedAt.ToLocalTime().Date;
                var today = DateTime.Today;
                return localDate == today ? "Bugün"
                    : localDate == today.AddDays(-1) ? "Dün"
                    : localDate.ToString("dd MMMM yyyy", CultureInfo.CurrentCulture);
            }
        }
    }
}
