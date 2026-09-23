# Mimari kararlar

## KÏOKU referanslı masaüstü tasarımı ve su senaryosu (2026-09-22)

Kullanıcı [KÏOKU tasarımını](https://www.behance.net/gallery/253369445/KIOKU-AI-powered-extension)
görsel referans olarak seçti. Yeni yön; açık/koyu nötr yüzeyler, kırmızı vurgu,
monospace etiketler, keskin kenarlar ve ince ayırıcılarla yoğun bilgiyi okunabilir tutmaktır.
Referansın logosu veya görselleri ürün varlığı olarak kopyalanmaz. WPF ve mevcut
bağımlılıklar korunur; yeni UI kütüphanesi gerekmez.

Su alanı kullanıcı isteğiyle eklenir, ancak günlükler ölçülmüş su verisi içermediği
için değiştirilebilir katsayılı senaryo olarak etiketlenir. Varsayılan 1 mL/1.000 token
örnek bir varsayımdır; Codex ölçümü değildir. Formül ve araştırma sınırı
[su metodolojisi](water-methodology.md) belgesindedir.

## C# + WPF + SQLite ile başlama (2026-09-18)

İlk uygulanabilir dil ve masaüstü yığını olarak C#/.NET 10, WPF ve SQLite seçildi. Hedef Windows olduğu için WPF, Windows masaüstü kontrolleri ve Visual Studio iş akışıyla doğal uyum sağlar. SQLite ise ayrı bir sunucu gerektirmeyen, yerel uygulama için hafif bir kalıcılık katmanıdır.

Kaynaklar:

- [WPF overview (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)
- [Microsoft.Data.Sqlite guidance (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/)

Bu seçim bilinçli olarak geri alınabilir tutuldu: alan modelleri ve repository sınırı Core/Infrastructure ayrımında, UI ise ayrı App projesindedir. Başlangıçta kütüphane yığını büyütülmedi; ihtiyaç ortaya çıkarsa alternatif UI veya veri katmanı bu sınırlar üzerinden değerlendirilebilir.

## Gerçek kullanıcı verisine erişmeme

Geliştirme ve testlerde gerçek Codex logları, auth klasörleri veya sırlar okunmaz; sentetik örnekler kullanılır. Uygulamada kullanıcı kaynak klasörünü seçip taramayı başlattığında günlükler salt okunur işlenir. `auth.json` hiçbir durumda okunmaz. Mesaj/araç içerikleri indekse kaydedilmez.

## İlk ürün için doğrudan SQLite erişimi (2026-09-22)

Ürün diliminde veri erişimi için doğrudan `Microsoft.Data.Sqlite` kullanılır; ayrı ORM veya ek repository framework'ü eklenmez. Bu yaklaşım WPF masaüstü uygulamasını küçük tutar, sorguların davranışını görünür kılar ve çalışan ürünü daha hızlı göstermeyi sağlar. İleride sorgu karmaşıklığı veya test maliyeti büyürse CommunityToolkit.Mvvm ve ORM seçenekleri yeniden değerlendirilebilir; şu an bunlar ertelenmiş seçeneklerdir.

## Paketleme kararını erteleme (2026-09-22)

Installer, MSIX ve self-contained dağıtım seçenekleri ilk çalışan ürün görüldükten sonra seçilecektir. Bu aşamada paketleme çıktısı üretilmez; Visual Studio ve `dotnet run` geliştirme akışı yeterlidir.

## Sürümlü yerel fiyat kataloğu (2026-09-22)

Uygulama açtığı her SQLite indeksine `2026-09-22-current-price-fallback-v2` sürümlü varsayılan fiyatları `INSERT OR IGNORE` ile ekler. Varsayılan satırların teknik başlangıç tarihi `1970-01-01` seçildi; böylece desteklenen eski oturumlar da 22 Eylül 2026'da doğrulanan güncel fiyatlarla bugünkü yaklaşık karşılık kazanır. Bu bir tarihsel fiyat iddiası değildir. Önceki sürümün yalnız `2026-09-22` satırını taşıyan mevcut DB'lerine fallback satırı idempotent olarak eklenir. Kullanıcının CSV ile yazdığı değer sonraki açılışta ezilmez. Fiyat seçimi, kullanım zamanından ileri olmayan en yeni `effective_date` satırını kullandığı için tarihli kullanıcı satırları fallback'in önüne geçer.

Katalog değerleri 22 Eylül 2026 tarihinde doğrulanan OpenAI standart API liste fiyatlarıdır. Bunlar tarihsel fiyat veya Codex abonelik faturası iddiası taşımaz; yalnız güncel API liste fiyatıyla yaklaşık karşılık üretir. Sadece model adı eşleşmeyen kullanımlar kısmi ya da bilinmeyen bırakılır. Çevrimiçi fiyat çekme, ilk ürün diliminde ağ bağımlılığı ve tarihsel belirsizlik yaratmamak için eklenmedi.
