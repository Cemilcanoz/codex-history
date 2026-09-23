# Su ayak izi senaryosu

Bu özellik ölçülmüş Codex su tüketimini göstermez. Yerel günlükler veri merkezini,
soğutma sistemini, elektrik kaynağını veya istek başına enerji tüketimini sağlamaz.
Bu nedenle uygulama, kullanıcının değiştirebildiği bir katsayıyla bir senaryo hesaplar.

`su (mL) = (girdi token + çıktı token) / 1000 × katsayı (mL / 1000 token)`

Başlangıçtaki **1 mL / 1.000 token**, yalnızca hesaplayıcının nasıl çalıştığını gösteren
örnek varsayımdır; bilimsel olarak doğrulanmış bir Codex katsayısı değildir. Sonuç bir
güven aralığı veya çevresel etki sertifikası olarak yorumlanmamalıdır. Önbellek tokenları
girdinin, reasoning tokenları çıktının alt kümesi olduğundan yeniden eklenmez.
Bu basit senaryo önbelleğin farklı enerji maliyetini ayrıca modellemez. Kısmi günlükler
eksik token toplamı ve dolayısıyla eksik senaryo sonucu üretebilir.

## Araştırma dayanağı ve sınırı

[Li ve arkadaşları, Making AI Less Thirsty](https://arxiv.org/abs/2304.03271),
su ayak izinin modelin çalıştığı yer ve zamana bağlı olduğunu ele alır.
[Google'ın üretim ortamı çalışması](https://arxiv.org/abs/2508.15734), belirli bir
Gemini metin isteği dağılımı için ölçüm yöntemi sunar. Bunların hiçbiri bu uygulamanın
izlediği Codex modelleri için token başına evrensel bir dönüşüm sağlamaz.
Kaynaklar örnek katsayının kaynağı değil, neden varsayımları açıkça göstermemiz
gerektiğinin dayanağıdır.

Kullanıcı kendi doğrulanmış enerji ve su yoğunluğu verisine sahipse katsayıyı buna
göre değiştirebilir. Model/konum ayrıntıları olmadan gerçek tüketim iddiası kurulmaz.
