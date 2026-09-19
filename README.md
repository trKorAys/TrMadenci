# TrMadenci

TrMadenci, Windows bilgisayarlarda ekran kartı ve işlemciyle kripto para madenciliğini
kolaylaştırmak için geliştirilen Türkçe bir madencilik uygulamasıdır.

Uygulama; kullanılacak coini seçmenizi, ekran kartlarınızı yönetmenizi, madenciliği
başlatıp duraklatmanızı ve sistem sağlığını tek ekrandan izlemenizi sağlar. Açıklanan
%0,75 developer fee dışında madencilik payları kullanıcının ayarladığı havuz hesabına
gönderilir.

> TrMadenci geliştirilmekte olan bir üretim adayıdır. Aşağıdaki destek tablosunda
> “Hazır” olarak işaretlenmeyen coinleri günlük gelir amacıyla kullanmayın.

## Destek durumu

| Coin | Algoritma | Donanım | Durum |
|---|---|---|---|
| Ravencoin (RVN) | KAWPOW | NVIDIA GPU | Hazır |
| Ethereum Classic (ETC) | ETCHash | NVIDIA GPU | Canlı çalışma başarılı; son 24 saatlik yeterlilik testi bekleniyor |
| Conflux (CFX) | Octopus | Yüksek belleğe sahip NVIDIA GPU | GPU ve canlı share yeterlilik testleri bekleniyor |
| Monero (XMR) | RandomX | CPU | Geliştirme aşamasında; madencilik kapalı |

AMD ve Intel ekran kartları için OpenCL altyapısı geliştirilmektedir; henüz normal
madencilik için etkin değildir.

## Öne çıkan özellikler

- Açılışta coin ve algoritma seçimi
- Tek veya birden fazla ekran kartı kullanabilme
- Havuz bağlantısı ve yedek havuz desteği
- Otomatik crash raporu ve kontrollü yeniden başlatma
- Anlık hashrate, kabul/red edilen share ve bağlantı bilgileri
- Sıcaklık, fan, güç tüketimi, bellek ve enerji takibi
- Klavyeden duraklatma ve devam ettirme
- Uzun süreli sağlık/yeterlilik testi kaydı
- Kullanıcı hesabı doğrulanamazsa güvenli şekilde durma
- Kullanıcının cüzdan veya worker bilgisini başka bir hesaba çevirmeme

## Sistem gereksinimleri

- 64 bit Windows 10 veya Windows 11
- Güncel NVIDIA ekran kartı sürücüsü
- Desteklenen bir NVIDIA ekran kartı
- Kararlı internet bağlantısı
- Yeterli güç kaynağı, kasa hava akışı ve soğutma

GitHub Releases bölümündeki hazır Windows paketi bağımsız çalışır; ayrıca .NET kurmanız
gerekmez. Kaynak koddan derleme yapmak isteyen geliştiriciler teknik belgelere
başvurabilir.

## Hızlı başlangıç

### 1. Programı indirin

[GitHub Releases](https://github.com/trKorAys/TrMadenci/releases) bölümünden en güncel
Windows paketini indirin ve ZIP dosyasını normal bir klasöre çıkarın. Programı ZIP
dosyasının içinden çalıştırmayın.

Yalnızca [TrMadenci’nin resmî GitHub sayfasından](https://github.com/trKorAys/TrMadenci)
paylaşılan paketleri kullanın. Test veya imzasız aday sürümlerde Windows güvenlik uyarısı
gösterebilir.

### 2. Havuz hesabınızı yazın

RVN için `trmadenci.example.json` dosyasının bir kopyasını oluşturun ve kopyanın adını
`trmadenci.json` yapın. Dosyayı Not Defteri ile açıp şu alanı kendi mining hesabınız ve
worker adınızla değiştirin:

```text
BINANCE_MINING_ACCOUNT.worker
```

Örnek:

```text
KullaniciAdi.WorkerAdi
```

Şablondaki diğer ayarları bilmiyorsanız değiştirmeyin. TrMadenci sizden hiçbir zaman
cüzdan seed kelimelerinizi veya private key’inizi istemez.

### 3. TrMadenci’yi başlatın

`run-trmadenci.bat` dosyasına çift tıklayın. Önce coin seçme ekranı açılır:

1. **Ravencoin** seçin.
2. **Supervisor ile madenciliği başlat** seçeneğini kullanın.
3. Ekranda görünen coin, ağ, algoritma, havuz ve kullanıcı worker bilgisini kontrol edin.

Supervisor seçeneği önerilir. Beklenmeyen bir native hata oluşursa rapor oluşturur ve
güvenli sınırlar içinde yeniden başlatmayı dener. Normal kapatma veya yanlış hesap
bilgileri sonsuz yeniden başlatma döngüsüne sokulmaz.

## Coin yapılandırmaları

Her coin kendi ayar dosyasını kullanır:

| Coin | Şablon | Kullanıcı dosyası |
|---|---|---|
| RVN | `trmadenci.example.json` | `trmadenci.json` |
| ETC | `trmadenci.etc.example.json` | `trmadenci.etc.local.json` |
| CFX | `trmadenci.cfx.example.json` | `trmadenci.cfx.local.json` |
| XMR | `trmadenci.xmr.example.json` | `trmadenci.xmr.local.json` |

Şablonu kopyaladıktan sonra yalnızca kendi havuz, cüzdan veya worker bilginizi girin.
Yerel ayar dosyaları GitHub’a gönderilmez ve yayın paketlerine dahil edilmez.

ETC, CFX ve XMR menülerindeki kısıtlamalar bilinçlidir. Bir coin gerekli testleri
tamamlamadan normal madencilik seçeneği açılmaz.

## Çoklu ekran kartı seçimi

Coin menüsündeki **GPU seçimini değiştir** seçeneği bilgisayardaki uygun ekran kartlarını
listeler. Tek GPU, birden fazla GPU veya tüm GPU’lar seçilebilir. Seçim ayar dosyasına
kaydedilir ve sonraki açılışlarda otomatik kullanılır.

Her ekran kartının sıcaklığı, fanı, güç tüketimi, bellek kullanımı, hashrate’i ve çalışma
durumu ayrı izlenir.

## Madencilik sırasında kontroller

Madencilik penceresi açıkken:

| Tuş | İşlem |
|---|---|
| `P` | Madenciliği duraklatır |
| `S` | En güncel işten devam eder |
| `D` | Güncel kontrol durumunu gösterir |
| `Ctrl+C` | Madenciliği güvenli biçimde kapatır |

Pause sırasında havuz bağlantısı açık kalır ancak hash aranmaz. Pause süresi developer
fee veya süreli test süresinden sayılmaz.

## Ekranda gösterilen bilgiler

TrMadenci konsolu aşağıdaki bilgileri birlikte gösterir:

- Seçilen coin, ağ ve algoritma
- Aktif havuz ve kullanıcı worker bilgisi
- Anlık ve ortalama hashrate
- Kabul edilen, reddedilen ve geçersiz share sayıları
- GPU sıcaklığı, fan hızı, güç ve bellek kullanımı
- Anlık güç tüketimi ve tahmini enerji kullanımı
- Pause, reconnect, recovery ve Supervisor durumu
- Kullanıcı/developer çalışma penceresi

## Developer fee

TrMadenci’nin resmî sürümünde developer fee oranı **%0,75**’tir.

- Fee, sabit bir saatte değil rastgele çalışma aralıklarında alınır.
- Seçilen coin ve algoritma değiştirilmez.
- Kullanıcının GPU’su gizlice başka bir coine geçirilmez.
- Developer hedefi başlangıç ekranında ve geçiş kayıtlarında gösterilir.
- Oran ve hedef normal kullanıcı ayarlarından değiştirilemez.
- Kullanıcı havuz hesabı eksik veya reddedilmişse developer hesabıyla madenciliğe devam
  edilmez; işlem güvenli şekilde durur.

Developer hedefi hazırlanmamış coinlerde madencilik zaten kapalıdır.

## Uzun süreli testler

ETC gibi üretim öncesi coinlerde menü, 24 saatlik soak/yeterlilik testi sunabilir. Bu
sürede normal madencilik yapılır ve kullanıcıya ait kabul edilmiş share’ler kullanıcı
hesabına gider.

Test süresi aktif madencilik süresidir. Pause verilen zaman toplam süreye eklenmez. Test
sonunda sıcaklık, güç, enerji, share, recovery ve hata durumlarını içeren bir sağlık
özeti oluşturulur.

Uzun testleri başlatmadan önce:

- İlk 30–60 dakika sıcaklık ve fan değerlerini gözlemleyin.
- Klimayı veya gerekli ortam soğutmasını kapatmayın.
- Güç kaynağının ve priz hattının yükü taşıyabildiğinden emin olun.
- Makineyi yüksek sıcaklıkta gözetimsiz bırakmayın.

## Güvenlik ve gizlilik

- Seed phrase, private key veya borsa şifrenizi programa yazmayın.
- Madencilik için yalnızca havuzun verdiği worker/cüzdan bilgisini kullanın.
- Ayar dosyanızı ve destek paketlerinizi paylaşmadan önce kişisel bilgileri kontrol edin.
- Bilinmeyen kişilerce yeniden paketlenmiş sürümleri çalıştırmayın.
- Ekran kartınıza uygun güç ve sıcaklık sınırlarını takip edin.

TrMadenci ekran kartına otomatik overclock uygulamaz ve kullanıcı onayı olmadan donanım
ayarlarını değiştirmez.

## Sık sorulan sorular

### Soak testi sırasında gerçekten madencilik yapılıyor mu?

Evet. Havuz tarafından kabul edilen kullanıcı share’leri yapılandırdığınız kullanıcı
hesabına gönderilir.

### Birden fazla ekran kartı kullanabilir miyim?

Evet. Coin menüsündeki GPU seçim ekranından istediğiniz cihazları seçebilirsiniz.

### Madenciliği geçici olarak durdurabilir miyim?

Evet. `P` ile duraklatabilir, `S` ile en güncel havuz işinden devam edebilirsiniz.

### Kazanç garantisi var mı?

Hayır. Kazanç; coin fiyatı, ağ zorluğu, havuz performansı, elektrik maliyeti ve donanıma
göre değişir. TrMadenci herhangi bir gelir veya yatırım getirisi garantisi vermez.

### Program cüzdanıma erişir mi?

Hayır. Madencilik için seed veya private key gerekmez. Program yalnızca ayar dosyasındaki
havuz worker/cüzdan kimliğini kullanır.

## Destek ve iletişim

- E-posta: [koray.altiner@outlook.com](mailto:koray.altiner@outlook.com)
- Yatırım analiz platformu: [www.yatirimiq.com](https://www.yatirimiq.com)
- GitHub: [github.com/trKorAys/TrMadenci](https://github.com/trKorAys/TrMadenci)

Destek talebinde bulunurken coin adı, kullanılan ekran kartı, hata mesajı ve mümkünse
Supervisor destek paketini ekleyin. Seed phrase, private key veya hesap şifresi
göndermeyin.

## Teknik belgeler

Son kullanıcıların bu belgeleri okuması gerekmez. Geliştiriciler ve katkıda bulunmak
isteyenler için:

- [Mimari](docs/architecture.md)
- [Coin ve algoritma yol haritası](docs/coin-algorithm-roadmap.md)
- [Sürüm ve yeterlilik durumu](docs/release-readiness.md)
- [Üçüncü taraf bildirimleri](THIRD-PARTY-NOTICES.md)

## Sorumluluk reddi

Kripto para madenciliği elektrik tüketir, donanımı ısıtır ve donanım ömrünü etkileyebilir.
Kullanıcı; elektrik, soğutma, donanım, havuz hesabı, yerel mevzuat ve vergi
yükümlülüklerinden kendisi sorumludur.

TrMadenci ve YatırımIQ tarafından sunulan bilgiler yatırım tavsiyesi değildir.
