# Deploy OMS ke Vercel

OMS menggunakan ASP.NET Core MVC, penyimpanan JSON privat di Vercel Blob, login lokal, dan Gmail SMTP. Aplikasi ini tidak membutuhkan Google Sheets, Apps Script, Google OAuth, maupun akun WOM Workspace.

> **Periksa dukungan runtime terlebih dahulu:** proyek .NET ini perlu dijalankan sebagai container dari `Dockerfile.vercel`. Deploy hanya dapat dilakukan jika custom/container runtime tersedia untuk akun dan region Vercel Anda. Jika opsi tersebut tidak tersedia, gunakan hosting container seperti Azure App Service, Google Cloud Run, atau layanan sejenis; Vercel Functions bukan pengganti langsung untuk aplikasi ini.

## 1. Siapkan Gmail untuk SMTP

1. Pastikan akun `outingmanagementsystem2026@gmail.com` mengaktifkan verifikasi 2 langkah.
2. Buat **App Password** untuk aplikasi OMS pada pengaturan keamanan akun Google. App Password bukan password login Gmail dan bukan OAuth client secret.
3. Simpan App Password sebagai secret. Jangan masukkan ke source code, GitHub, atau `appsettings.json`.
4. Akun Google harus mengizinkan SMTP dan kuota emailnya mencukupi. Pengiriman memakai `smtp.gmail.com` port `587` dengan STARTTLS.

## 2. Buat penyimpanan JSON privat

1. Buat Vercel Blob Store pada team/project yang akan menjalankan OMS.
2. Pastikan akses blob tetap **private**.
3. Hubungkan Blob Store ke project. Vercel akan menyediakan `BLOB_READ_WRITE_TOKEN`; beri token tersebut izin baca/tulis hanya untuk store yang dipakai OMS.
4. Database akan dibuat otomatis di `oms/oms-database.json`. Key ring untuk cookie login tersimpan di `oms/data-protection-keyring.json`.

Token Blob bersifat rahasia. Jangan membuat database publik atau menyimpan token di repository. Saat menggunakan Vercel Blob, jangan menghapus atau mengganti file key ring: cookie yang dibuat dengan key ring lama tidak akan dapat dibuka setelah key tersebut hilang.

## 3. Siapkan repository dan project Vercel

1. Push repository ke GitHub tanpa file rahasia atau database produksi.
2. Di Vercel, pilih **Add New → Project**, lalu import repository.
3. Atur **Root Directory** ke folder yang berisi `OMS.csproj` dan `Dockerfile.vercel` (folder `OMS` di root repository).
4. Gunakan konfigurasi container yang didukung Vercel. `Dockerfile.vercel` membangun aplikasi .NET 10 dan menjalankannya pada port `8080`; aplikasi membaca port dari environment `PORT` melalui konfigurasi ASP.NET.
5. Jika Vercel tidak menawarkan custom/container runtime untuk project Anda, jangan deploy sebagai static site atau Vercel Function. Gunakan layanan container yang mendukung image Docker.

## 4. Tambahkan Environment Variables

Di **Project → Settings → Environment Variables**, atur sekurangnya variabel berikut untuk Production. Atur juga untuk Preview hanya jika Preview memang akan dipakai, dan sebaiknya gunakan Blob Store serta kredensial uji yang terpisah.

| Variable | Nilai |
| --- | --- |
| `BLOB_READ_WRITE_TOKEN` | Token privat yang diberikan Vercel Blob |
| `OmsAuth__BootstrapEmail` | Email admin OMS pertama |
| `OmsAuth__BootstrapPassword` | Password awal yang kuat, minimum 12 karakter |
| `OmsPublic__BaseUrl` | Domain utama OMS, misalnya `https://oms.example.com` |
| `Email__Host` | `smtp.gmail.com` |
| `Email__Port` | `587` |
| `Email__Username` | `outingmanagementsystem2026@gmail.com` |
| `Email__Password` | Gmail App Password yang dibuat pada langkah 1 |
| `Email__SenderName` | `Outing Management System` |

Pastikan App Password disalin tanpa spasi pemisah yang ditampilkan Google. Jangan set `OmsDatabase__LocalPath` pada Vercel: filesystem container tidak persisten, sedangkan OMS mewajibkan Blob di Production.

## 5. Deploy dan inisialisasi

1. Deploy project dan tunggu hingga deployment berstatus Ready.
2. Buka URL production dan masuk dengan email/password bootstrap yang dikonfigurasi.
3. Pada database JSON yang masih kosong, OMS membuat tabel dan akun bootstrap `SUPERADMIN` secara otomatis. Kredensial bootstrap hanya digunakan saat belum ada pengguna; mengubah environment variable kemudian tidak akan mereset password database yang sudah terbentuk.
4. Muat data master karyawan melalui fitur unggah CSV pada halaman Peserta. Gunakan header kolom yang didukung OMS, sesuai template/ekspor master karyawan yang tersedia di aplikasi.
5. Buat kegiatan dan peserta dari aplikasi, lalu uji check-in dan pengiriman undangan ke alamat email yang dapat Anda akses.

Simpan email/password admin dengan aman. Jika password admin terlupa, environment bootstrap tidak mereset akun yang sudah tersimpan; lakukan prosedur pemulihan yang sesuai dan jangan menghapus Blob database secara sembarang.

## Data lama dan operasional

- **Data lama dari Google Sheets tidak otomatis dipindahkan.** Deployment pertama membuat file JSON baru yang berisi tabel kosong dan akun SUPERADMIN awal. Impor master karyawan tersedia melalui CSV; kegiatan, peserta, sesi, kehadiran, dan audit lama perlu diekspor/dikonversi ke format JSON OMS dengan prosedur migrasi terpisah bila ingin dipertahankan.
- Database dan key ring berada pada Blob privat yang sama. Kehilangan atau penghapusan database menghilangkan data operasional; kehilangan key ring dapat membuat sesi/cookie lama tidak dapat dibuka.
- OMS mengamankan penyimpanan dengan ETag/conditional write agar perubahan dari salinan database yang sudah usang tidak diam-diam menimpa versi yang lebih baru. Jika beberapa instance menulis bersamaan, operasi yang berbenturan dapat gagal dan perlu dicoba ulang setelah data terbaru dimuat.
- Login memakai akun lokal OMS, bukan akun Google. Gunakan satu akun SUPERADMIN bersama untuk panitia; menu dan endpoint pembuatan/impor akun panitia tidak tersedia. Karena kredensial digunakan bersama, semua pemegangnya memiliki akses SUPERADMIN dan aktivitas tidak dapat dibedakan berdasarkan pengguna.
- SUPERADMIN/ADMIN dapat mengedit kegiatan dari **Kegiatan & Laporan**. Penghapusan peserta menghapus data peserta, riwayat kehadiran, QR, dan log terkait. Penghapusan kegiatan menghapus kegiatan beserta peserta, sesi, kehadiran, dan log yang terkait; data master karyawan global tidak ikut dihapus.
- SUPERADMIN/ADMIN mengelola beberapa hari rundown pada bagian **Rundown kegiatan** di **Kegiatan & Laporan**. Masukkan waktu lokal Jakarta untuk setiap agenda; waktu yang saling bertumpang tindih dalam satu kegiatan ditolak. Peserta dapat membuka rundown publik di `/event/{eventId}/schedule`; status agenda dan waktu berjalan mengikuti WIB dan halaman diperbarui otomatis. Tautan rundown disertakan pada email undangan. Atur `OmsPublic__BaseUrl` ke domain publik HTTPS OMS sebelum mengirim email di Production.
- Email undangan menyertakan tautan rundown personal dengan fragmen token QR peserta. Setelah tautan dibuka, halaman mengambil QR yang sama tanpa menaruh token pada query URL. Token merupakan kredensial QR: peserta dapat membagikan tautan tersebut kepada panitia untuk bantuan, tetapi jangan mengirimnya pada kanal publik. Mengirim ulang undangan menerbitkan QR baru sehingga tautan/QR sebelumnya tidak berlaku.
- SMTP hanya bisa diuji dengan deployment yang memiliki environment variables Gmail valid; jangan mengirim data sensitif saat uji coba.

## Pengujian lokal

Untuk menjalankan secara lokal, set `OmsAuth__BootstrapEmail`, `OmsAuth__BootstrapPassword`, `Email__Password` (bila menguji email), lalu jalankan aplikasi dari folder project `OMS`. Tanpa `BLOB_READ_WRITE_TOKEN`, database lokal berada di `OMS/App_Data/oms-database.json` (folder di dalam project), sedangkan key ring lokal berada di `%LOCALAPPDATA%\OMS`; gunakan penyimpanan lokal hanya untuk development, bukan deployment Production. Folder `App_Data` diabaikan oleh Git karena berisi data peserta dan kredensial hash.

Untuk mengganti password login akun SUPERADMIN yang sudah tersimpan di database lokal, jalankan dari folder project `OMS\OMS`:

```powershell
dotnet run -- --reset-superadmin-password outingmanagementsystem2026@gmail.com
```

Perintah meminta password baru dua kali tanpa menampilkannya, mensyaratkan 12–128 karakter, dan membuat salinan cadangan file database sebelum perubahan. Perintah ini hanya bekerja untuk database lokal; jangan atur `BLOB_READ_WRITE_TOKEN` saat menjalankannya. Perubahan password login OMS tidak mengubah password Gmail atau App Password SMTP.
