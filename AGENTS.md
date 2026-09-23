# Codex-History çalışma sözleşmesi

## Kapsam

Bu depo, Windows üzerinde Codex geçmişini yerel ve aranabilir hale getiren küçük bir .NET uygulamasıdır. Uygulama kararları ve kalıcı kullanıcı tercihleri için CemoOS bellek köprüsü:

`C:\Users\cemil\OneDrive\Desktop\CemoOS\MEMORY.md`

Oturum başında bu hafıza sözleşmesini okuyun ve uygulayın. Anlamlı oturum sonunda CemoOS devir kayıtlarını mevcut içeriği ve başlık biçimlerini koruyarak güncelleyin. Ham günlük, kimlik doğrulama verisi veya parola depoya ya da hafızaya eklenmez.

## Geliştirme ilkeleri

- Hedef platform Windows; arayüz WPF, kalıcı veri SQLite'tır.
- .NET 10 SDK ve Visual Studio ile derlenebilir, küçük ve anlaşılır bir çözüm korunur.
- Geliştirme ve testlerde sentetik kayıtlar kullanılır. Uygulama yalnız kullanıcının seçip taramayı başlattığı kaynak günlüklerini salt okunur işler; `auth.json` ve kimlik bilgileri her durumda kapsam dışıdır.
- Astra orkestrasyon ve inceleme yapar; uygulama kodunu karmaşık işlerde Sol, basit işlerde Luna yazar.
- GitHub deposu, remote ve push işlemlerini kullanıcı en son elle yapacaktır; bu işlemleri başlatmayın.
- Gereksiz kütüphane eklenmez; geri alınabilir, küçük değişiklikler tercih edilir.
- `src/CodexHistory.Core` alan/kurallar, `Infrastructure` SQLite erişimi, `App` WPF ve `Cli` komut satırı katmanıdır.
- Kaynak veya test değişikliğinde ilgili doğrulamalar çalıştırılır; kök belgeler güncel tutulur.

## Doğrulama

Kökten `dotnet build CodexHistory.sln` çalıştırın. CLI yardımını görmek için `dotnet run --project src/CodexHistory.Cli -- --help` kullanın.
