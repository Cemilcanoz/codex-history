# Codex-History

Codex-History, Windows üzerinde Codex oturum geçmişini yerel olarak inceleyen .NET 10 uygulamasıdır. Kaynak günlükleri arşivlemez; oturum, görev, model ve maliyet metadatasını yeniden üretilebilir bir SQLite indeksinde tutar.

Bağımsız bir topluluk projesidir (unofficial community project); OpenAI'nin resmî ürünü değildir.

## Durum ve mimari

İlk ürün dilimi .NET 10 + WPF + doğrudan `Microsoft.Data.Sqlite` ile kuruluyor. Çözüm beş küçük projeye ayrılır:

- `CodexHistory.Core`: geçmiş kayıtlarının modelleri ve alan kuralları.
- `CodexHistory.Infrastructure`: SQLite kalıcılığı ve sorgu erişimi.
- `CodexHistory.App`: WPF masaüstü arayüzü.
- `CodexHistory.Cli`: inceleme ve tanılama için komut satırı yüzeyi.
- `CodexHistory.Tests`: sentetik veriyle regresyon testleri.

Veri akışı `CLI/WPF → Core → Infrastructure → yerel SQLite` şeklindedir. Ek ORM veya MVVM paketi kullanılmıyor; alternatifler [`docs/decisions.md`](docs/decisions.md) içinde kayıtlıdır.

## Visual Studio ile açma

Visual Studio Community 2026'da `CodexHistory.sln` dosyasını açın. Başlangıç projesi olarak `CodexHistory.App` seçildiğinde F5 ile WPF uygulaması çalışır.

## Komut satırından derleme, test ve çalıştırma

```powershell
dotnet build CodexHistory.sln
dotnet test CodexHistory.sln
dotnet run --project src/CodexHistory.Cli -- --help
dotnet run --project src/CodexHistory.Cli -- --source "C:\path\to\selected-sessions" --db "$env:TEMP\codex-history\index.db"
dotnet run --project src/CodexHistory.App
```

CLI `--source` ile açıkça verilen klasörü tarar ve `--db` ile belirtilen SQLite dosyasını yeniler. UI smoke çalıştırması `./scripts/Run-Demo.ps1` ile yapılır; App kendi sentetik kaynağını kullanır ve `artifacts/demo.png` çıktısını doğrular. Farklı çıktı yolu için `./scripts/Run-Demo.ps1 -ArtifactPath artifacts\demo-script.png` kullanabilirsiniz.

## Arayüz kullanımı

Önce kaynak klasörü seçilir, ardından tarama başlatılır. Tarama bittiğinde ilk görünür oturum otomatik seçilir; zaman çizelgesi ve günlükte varsa birincil/ikincil kota anlık görüntüleri ayrıntıda gösterilir. Liste; görev başlığı, zaman, model, durum ve tahmini maliyet metadatasını gösterir. Ham prompt, araç çıktısı ve konuşma metni varsayılan olarak gösterilmez.

## Fiyat kataloğu ve maliyet sınırı

Uygulama `2026-09-22-current-price-fallback-v2` sürümlü yerel fiyat kataloğunu yeni ve mevcut indekslere idempotent olarak ekler. Katalog; `gpt-6-astra`, `gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna`, `gpt-5.5`, `gpt-5.3-codex` ve `gpt-5.2-codex` için 1 milyon token başına input, cached input ve output fiyatlarını içerir. Kaynak, 22 Eylül 2026 tarihinde doğrulanan OpenAI standart API liste fiyatlarıdır. Varsayılan satırlar teknik olarak `1970-01-01` fallback tarihiyle saklanır; amaç desteklenen eski oturumları tarihsel fiyat iddiası kurmadan bugünkü liste fiyatıyla karşılaştırmaktır.

Gösterilen para değeri gerçek Codex abonelik faturası veya tarihsel fatura değil, güncel standart API liste fiyatıyla yaklaşık karşılıktır. Yalnız eşleşmeyen model kullanımları varsa tutar `kısmi` olarak işaretlenir; hiç model eşleşmesi yoksa `Bilinmiyor` gösterilir. Kullanıcı, başlığı `model,effective_date,input_per_million,cached_per_million,output_per_million` olan bir CSV içe aktarabilir. Fiyat seçimi kullanım tarihinden ileri olmayan en yeni satırı kullandığı için tarihli CSV satırları fallback kataloğunun önüne geçer; aynı model/tarih tekrar içe aktarılırsa önceki değer güncellenir. Örnek dosya [`samples/prices.example.csv`](samples/prices.example.csv) içindedir.

## Su ayak izi senaryosu

Su alanı ölçülmüş tüketimi değil, seçili filtredeki girdi ve çıktı tokenları üzerinden
hesaplanan bir senaryoyu gösterir. Katsayı kullanıcı tarafından değiştirilebilir.
Başlangıçtaki **1 mL / 1.000 token yalnız örnek varsayımdır**; Codex'e ait doğrulanmış
bir katsayı değildir. Önbellek ve reasoning tokenları toplama ikinci kez eklenmez.
Formül, belirsizlikler ve araştırma bağlantıları [metodoloji belgesindedir](docs/water-methodology.md).

## Gizlilik ve sınırlar

Uygulama yalnızca kullanıcının seçip taramayı başlattığı yerel klasörü okur. `auth.json`, kimlik bilgileri ve sırlar hiçbir koşulda okunmaz veya SQLite'a yazılmaz. Demo kayıtları sentetiktir. Bu sürüm çevrimiçi senkronizasyon, bulut depolama, kesin fatura/kota ölçümü ve hassas veri maskeleme sağlamaz. Paketleme/installer kararı çalışan ilk ürün görüldükten sonra verilecektir.

Kararların gerekçeleri için [`docs/decisions.md`](docs/decisions.md) dosyasına bakın.
