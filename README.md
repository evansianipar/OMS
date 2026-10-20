# OMS .NET — deployment dan akses Google Sheets

Project ini adalah migrasi MVP OMS dari Apps Script ke ASP.NET Core MVC .NET 10. Data tetap berada di spreadsheet `1iOa04OTUX2lAX9vVqESCXpSToWDn7jjcqHVgtWsWPw8`; tidak ada SQL/EF Core atau database lokal.

Fitur yang sudah dipindahkan pada fondasi ini:

- Login Google OpenID Connect, dibatasi ke domain `wom.co.id` dan allowlist aktif sheet `Users`.
- Dashboard statistik, kehadiran per departemen, scan terbaru, dan peserta belum hadir.
- Pemilihan dan pembuatan kegiatan, impor master karyawan, daftar/pencarian peserta, dan unduh QR individual.
- Check-in QR maupun manual berdasarkan pencarian NIK, validasi sesi dan token, deduplikasi, audit log, serta tabel laporan kehadiran dengan ekspor CSV.
- Halaman kerja terpisah untuk scanner, peserta/QR, serta gabungan daftar kegiatan dan laporan; fungsi utama tidak lagi muncul sebagai modal.
- Pengiriman undangan QR melalui Apps Script `MailApp.sendEmail` menggunakan akun `itengineer@wom.co.id`; aplikasi .NET memanggil fungsi Apps Script yang memvalidasi QR dan role sebelum mengirim.
- Scanner kamera live pada browser halaman .NET, serta scan dari gambar QR sebagai fallback.
- `Lock` proses untuk deduplikasi check-in dalam satu instance aplikasi.

Belum dipindahkan pada tahap ini: edit/nonaktifkan peserta/kegiatan, CRUD sesi melalui UI, manajemen pengguna melalui UI, serta modul transportasi, akomodasi, perlengkapan, konsumsi, games, pengumuman, dan ekspor PDF/ZIP.

## Dua proses Google

Login Workspace membuktikan identitas panitia, tetapi tidak otomatis memberikan server .NET hak baca spreadsheet. OMS menggunakan dua proses terpisah:

1. Login Google untuk panitia, dibatasi ke domain `wom.co.id` dan row aktif di `Users`.
2. Persetujuan OAuth oleh `itengineer@wom.co.id` agar server membaca/menulis spreadsheet dan menjalankan fungsi Apps Script untuk mengirim undangan. Panitia tidak diminta memberi scope Google API.

Tidak ada service account, pembagian file eksternal, penyimpanan password, atau domain-wide delegation. Refresh token disimpan terenkripsi oleh ASP.NET Data Protection di profil akun OS yang menjalankan aplikasi. Jangan menyalin password, cookie, OAuth client secret, atau token ke source code/repository.

## Persiapan Google

### A. OAuth client

1. Gunakan OAuth 2.0 **Web application client**. Aktifkan **Google Sheets API** dan **Apps Script API** pada project Google Cloud yang sama dengan OAuth client.
2. Tambahkan authorized redirect URI `https://<domain-hosting-OMS>/signin-google` dan `https://<domain-hosting-OMS>/signin-google-sheets`. Untuk development profile HTTPS project ini, tambahkan `https://localhost:7172/signin-google` dan `https://localhost:7172/signin-google-sheets`.
3. Simpan Client ID dan Client Secret hanya pada User Secrets, environment variable, atau secret manager.
4. OMS meminta scope `https://www.googleapis.com/auth/spreadsheets` dan `https://www.googleapis.com/auth/script.send_mail` saat pemilik menghubungkan akun. Scope `script.send_mail` dipakai Apps Script untuk mengirim email; aplikasi tidak menggunakan Gmail API.

### B. Siapkan Apps Script sebagai API executable

1. Buka project Apps Script yang berisi `QRService.gs` dan fungsi `sendInvitationMail`. Di **Project Settings**, hubungkan project ke **standard Google Cloud project** yang sama dengan OAuth Web Client. Apps Script API dan OAuth client harus berada pada project Cloud yang sama.
2. Pada Google Cloud project tersebut, pastikan **Apps Script API** dan **Google Sheets API** aktif.
3. Deploy Apps Script melalui **Deploy → New deployment → API executable**. Batasi **Who has access** ke pengguna dalam domain organisasi (`wom.co.id`); jangan pilih akses publik.
4. Pastikan `itengineer@wom.co.id` terdaftar aktif di sheet `Users` dengan role `ADMIN` atau `SUPERADMIN`. Fungsi Apps Script memeriksa role pemilik OAuth, token QR peserta, dan kuota email sebelum mengirim.
5. Salin **Script ID** dari **Project Settings** Apps Script. Isi pada konfigurasi `GoogleWorkspace:AppsScriptId` di host OMS. Script ID berbeda dari Deployment ID dan Spreadsheet ID.

### C. Hubungkan akun spreadsheet dan Apps Script

1. Jalankan OMS melalui HTTPS. Pada development lokal, browser menerima `https://localhost`.
2. Buka `/account/connect-sheets` dan login sebagai `itengineer@wom.co.id`. Jika sudah pernah menghubungkan Sheets sebelumnya, buka kembali alamat tersebut untuk menyetujui scope `script.send_mail`.
3. Setujui akses Google Sheets dan Apps Script untuk mengirim email. Aplikasi memverifikasi email akun tersebut dan menyimpan refresh token terenkripsi pada mesin server. Setelah halaman mengonfirmasi selesai, panitia dapat login dari halaman utama.
4. Jangan hapus token atau kunci Data Protection OS; OMS memerlukan keduanya untuk mempertahankan akses setelah restart. Di hosting dengan filesystem sementara, sediakan persistent storage untuk profil aplikasi dan key ring Data Protection sesuai prosedur keamanan organisasi.

## Konfigurasi environment

Spreadsheet ID, domain, dan email akun spreadsheet sudah diset pada `appsettings.json`. Atur OAuth secrets melalui environment variable atau secret manager:

- `Authentication__Google__ClientId`
- `Authentication__Google__ClientSecret`
- `GoogleWorkspace__AppsScriptId` — Script ID dari Apps Script Project Settings.

Development lokal dapat memakai User Secrets (jalankan dari folder project `OMS/OMS`):

```powershell
dotnet user-secrets init
dotnet user-secrets set "Authentication:Google:ClientId" "OAUTH_CLIENT_ID"
dotnet user-secrets set "Authentication:Google:ClientSecret" "OAUTH_CLIENT_SECRET"
dotnet run
```

Saat sign-in, pengguna harus terdaftar pada sheet `Users` dengan email Workspace, `IsActive=TRUE`, serta role yang didukung: `SUPERADMIN`, `ADMIN`, `REGISTRATION`, atau `VIEWER`. Role diperiksa ulang dari spreadsheet pada request terproteksi. Endpoint check-in hanya mengizinkan `SUPERADMIN`, `ADMIN`, dan `REGISTRATION`.

## Kamera dan hosting

`getUserMedia` berjalan pada halaman first-party ASP.NET, bukan iframe HTML Service Apps Script. Gunakan HTTPS di staging/produksi; localhost juga dianggap secure context oleh browser untuk development. Berikan izin kamera pada domain hosting dan tutup aplikasi lain yang mungkin sedang memakai kamera. Library `html5-qrcode` dimuat dari CDN versi 2.3.8; untuk lingkungan tanpa akses CDN, vendor file library ke `wwwroot` setelah meninjau lisensi dan proses update.

Jika hosting OMS sendiri memasang aplikasi dalam iframe, kebijakan `Permissions-Policy` iframe/host tetap harus mengizinkan kamera. Gunakan halaman standalone bila memungkinkan.

## Konsistensi Sheets dan batas concurrency

Check-in diserialisasi dengan `SemaphoreSlim` pada satu proses web, kemudian token hash divalidasi, duplikasi diperiksa, dan transaksi di-append ke sheet Attendance. Ini **bukan lock distributed**: untuk deployment multi-instance/load-balanced, aktifkan satu instance aplikasi untuk scanner atau tambahkan distributed lock yang sesuai. Google Sheets bukan database transaksional; backup spreadsheet dan rekonsiliasi tetap dibutuhkan.

Tidak ada aplikasi yang sudah dideploy otomatis. Build dan uji di environment organisasi, pastikan koneksi OAuth spreadsheet berfungsi, lalu deploy hosting HTTPS dengan akses jaringan internal sesuai kebijakan.
