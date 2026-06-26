# 🚀 GoBurhan - Yüksek Performanslı Link Kısaltma ve Analitik Servisi

**GoBurhan**, ASP.NET Core Minimal API, PostgreSQL ve Redis teknolojileriyle geliştirilmiş, yüksek performanslı ve modern bir link kısaltma ve analitik takip servisidir. 

Proje, hem şık bir yönetici arayüzü (SPA) hem de Telegram Bot entegrasyonu sunarak linklerinizi dilediğiniz yerden yönetmenizi ve tıklama analitiklerini anlık takip etmenizi sağlar.

---

## 🎯 Projenin Amacı ve Ne İşe Yarar?

İnternet üzerindeki uzun linklerinizi kısaltarak daha estetik ve paylaşılabilir hale getirir. Klasik link kısaltıcıların aksine, aşağıdaki kritik işlevleri tamamen bağımsız (self-hosted) olarak kendi sunucunuzda yürütmenizi sağlar:

*   **Hız Odaklı Yönlendirme:** Link yönlendirmeleri Redis önbelleği (Cache) üzerinden yapılarak milisaniyeler içinde gerçekleşir.
*   **Detaylı Ziyaretçi Analitiği:** Linklerinize tıklayan kişilerin cihaz tipleri, tarayıcıları, işletim sistemleri, referrer (yönlendiren site) bilgileri ve tıklama zamanları analiz edilerek kaydedilir.
*   **Telegram Bot Entegrasyonu:** Sunucuya veya web arayüzüne girmeden, Telegram botuna göndereceğiniz basit komutlarla hızlıca yeni kısa linkler üretebilir ve tıklama istatistiklerini raporlayabilirsiniz.
*   **Asenkron Analitik İşleme:** Linke tıklayan kullanıcıyı bekletmemek adına analitik verileri arka planda bir kuyrukta (`AnalyticsQueue`) toplanır ve arka plan servisleri (`BackgroundWorker`) vasıtasıyla veritabanına asenkron olarak yazılır.

---

## 🛠️ Teknolojik Altyapı

*   **Backend:** .NET 8 / 9 (ASP.NET Core Minimal APIs)
*   **Veritabanı:** PostgreSQL (Entity Framework Core ile yönetilir, otomatik migration desteği)
*   **Önbellekleme & Hız:** StackExchange.Redis (Hata toleranslı/resilient bağlantı desteğiyle Redis kapansa dahi sistem çalışmaya devam eder)
*   **Arka Plan Servisleri (Background Services):** 
    *   `AnalyticsBackgroundWorker` (Ziyaretçi verilerini asenkron işleme)
    *   `TelegramBotBackgroundWorker` (Telegram Bot API entegrasyonu)
*   **Güvenlik:**
    *   **Rate Limiting:** IP tabanlı istek limitleme (Admin ve Yönlendirme politikaları ayrı ayrı yönetilir)
    *   **Password Hashing:** PBKDF2/Salt tabanlı güvenli şifreleme.
    *   **Token Tabanlı Oturum Yönetimi:** Redis üzerinde saklanan güvenli oturum tokenları.
*   **Frontend:** Tek sayfa uygulaması (Single Page Application - SPA), `wwwroot/index.html` içinde gömülü modern ve responsive arayüz.

---

## 🚀 Öne Çıkan Özellikler

1.  **Redis Cache & Fallback:** Kısaltılmış linkler Redis'te önbelleğe alınır. Eğer Redis sunucusu çökerse, uygulama PostgreSQL üzerinden sorunsuz bir şekilde yönlendirme yapmaya devam eder (Fault tolerance).
2.  **Kuyruk Tabanlı Loglama:** Yoğun trafik altında dahi veritabanı darboğazını önlemek için asenkron loglama kuyruğu kullanılır.
3.  **Yönetici Arayüzü:**
    *   Linklerin toplam tıklama sayılarını görme.
    *   Referrer ve Tarayıcı istatistik grafiklerini inceleme.
    *   Hızlıca yeni link üretme, düzenleme ve silme işlemleri.
4.  **Telegram Bot Komutları:**
    *   Telegram botu üzerinden anlık link kısaltma.
    *   Oluşturulan linklerin analiz özetini çekme.

---

## ⚙️ Kurulum ve Çalıştırma

### 1. Gereksinimler
*   [.NET 8.0 veya üzeri SDK](https://dotnet.microsoft.com/download)
*   PostgreSQL Veritabanı
*   Redis Sunucusu (İsteğe bağlı, kurulmazsa sistem doğrudan DB üzerinden çalışır)
*   Docker (İsteğe bağlı - docker-compose.yml mevcuttur)

### 2. Veritabanı ve Bağlantı Ayarları
`appsettings.json` dosyasını kendi bilgilerinize göre düzenleyin:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=goburhan_db;Username=postgres;Password=YOUR_PASSWORD"
  },
  "Redis": {
    "ConnectionString": "localhost:6379"
  },
  "Telegram": {
    "BotToken": "YOUR_TELEGRAM_BOT_TOKEN",
    "AuthorizedUserId": 123456789
  }
}
```

### 3. Çalıştırma
Projeyi derlemek ve yerelde ayağa kaldırmak için terminalde şu komutları çalıştırın:

```bash
dotnet restore
dotnet run
```

Proje varsayılan olarak `http://localhost:5000` (veya belirtilen SSL portundan) yayına başlayacaktır. İlk açılışta veritabanı tabloları (EF Core Migrations) otomatik olarak oluşturulur.