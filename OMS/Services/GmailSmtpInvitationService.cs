using Microsoft.Extensions.Options;
using OMS.Configuration;
using OMS.Models;
using QRCoder;
using System.Net;
using System.Net.Mail;

namespace OMS.Services;

public sealed class GmailSmtpInvitationService(IOptions<EmailOptions> options)
{
    private readonly EmailOptions _options = options.Value;

    public async Task SendAsync(
        OmsEvent outing,
        OmsParticipant participant,
        string token,
        string camp,
        string transport,
        string vehicle,
        string scheduleUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Username) || string.IsNullOrWhiteSpace(_options.Password))
            throw new InvalidOperationException(
                "Konfigurasi email belum lengkap. Atur Email__Username dan Email__Password di environment server.");
        if (_options.Port is < 1 or > 65535)
            throw new InvalidOperationException("Konfigurasi SMTP port tidak valid.");

        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(token, QRCodeGenerator.ECCLevel.Q);
        var qrBytes = new PngByteQRCode(qrCodeData).GetGraphic(8);
        using var qrStream = new MemoryStream(qrBytes);
        using var message = new MailMessage
        {
            From = new MailAddress(_options.Username, _options.SenderName),
            Subject = $"Undangan {outing.EventName} - QR Check-in dan Check-out",
            Body = BuildPlainText(outing, participant, camp, transport, vehicle, scheduleUrl),
            IsBodyHtml = false
        };
        message.To.Add(new MailAddress(participant.Email.Trim()));

        var htmlView = AlternateView.CreateAlternateViewFromString(
            BuildHtml(outing, participant, camp, transport, vehicle, scheduleUrl),
            null,
            "text/html");
        var qrResource = new LinkedResource(qrStream, "image/png")
        {
            ContentId = "participantQr",
            TransferEncoding = System.Net.Mime.TransferEncoding.Base64
        };
        htmlView.LinkedResources.Add(qrResource);
        message.AlternateViews.Add(htmlView);

        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = true,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(_options.Username, _options.Password),
            Timeout = 30_000
        };
        cancellationToken.ThrowIfCancellationRequested();
        await client.SendMailAsync(message, cancellationToken);
    }

    private static string BuildPlainText(
        OmsEvent outing,
        OmsParticipant participant,
        string camp,
        string transport,
        string vehicle,
        string scheduleUrl) =>
        $"Yth. {participant.FullName},\r\n\r\n" +
        $"Anda terdaftar pada kegiatan {outing.EventName}.\r\n" +
        $"Tanggal: {FormatDate(outing.StartDate)}\r\n" +
        $"Lokasi: {Fallback(outing.Location, "Akan diinformasikan")}\r\n" +
        $"Kode peserta: {participant.EmployeeNumber}\r\n" +
        $"Camp / nomor kamar: {Fallback(camp, "Belum ditentukan")}\r\n" +
        $"Transportasi: {Fallback(transport, "Belum ditentukan")}\r\n" +
        $"Nomor kendaraan: {Fallback(vehicle, "Belum ditentukan")}\r\n\r\n" +
        $"Lihat rundown kegiatan: {scheduleUrl}\r\n\r\n" +
        "Tunjukkan QR Code pada email ini kepada panitia saat check-in dan check-out.";

    private static string BuildHtml(
        OmsEvent outing,
        OmsParticipant participant,
        string camp,
        string transport,
        string vehicle,
        string scheduleUrl) =>
        "<!doctype html><html lang=\"id\"><body style=\"margin:0;padding:0;background-color:#f2f5fa;font-family:Arial,Helvetica,sans-serif;color:#24324b\">" +
        "<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"background-color:#f2f5fa;padding:28px 12px\">" +
        "<tr><td align=\"center\">" +
        "<table role=\"presentation\" width=\"600\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"width:100%;max-width:600px;background-color:#ffffff;border:1px solid #e3e9f2;border-radius:12px;overflow:hidden\">" +
        "<tr><td bgcolor=\"#2457a7\" style=\"padding:26px 32px;color:#ffffff\">" +
        "<div style=\"font-size:12px;letter-spacing:1.6px;font-weight:bold;color:#dbeafe\">OUTING MANAGEMENT SYSTEM</div>" +
        "<div style=\"font-size:24px;line-height:1.3;font-weight:bold;margin-top:10px\">Undangan Kegiatan</div>" +
        "</td></tr>" +
        "<tr><td style=\"padding:28px 32px 12px\">" +
        $"<p style=\"font-size:16px;margin:0 0 12px\">Halo <strong>{Html(participant.FullName)}</strong>,</p>" +
        $"<p style=\"font-size:14px;line-height:1.7;color:#52627a;margin:0 0 22px\">Anda telah terdaftar sebagai peserta kegiatan <strong style=\"color:#24324b\">{Html(outing.EventName)}</strong>. Berikut detail kegiatan Anda:</p>" +
        "<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"background-color:#f7f9fc;border:1px solid #e7edf5;border-radius:8px\">" +
        DetailRow("Tanggal", FormatDate(outing.StartDate)) +
        (string.IsNullOrWhiteSpace(outing.EndDate) || outing.EndDate == outing.StartDate
            ? string.Empty
            : DetailRow("Sampai", FormatDate(outing.EndDate))) +
        DetailRow("Lokasi", Fallback(outing.Location, "Akan diinformasikan")) +
        DetailRow("Kode peserta", participant.EmployeeNumber) +
        DetailRow("Camp / nomor kamar", Fallback(camp, "Belum ditentukan")) +
        DetailRow("Transportasi", Fallback(transport, "Belum ditentukan")) +
        DetailRow("Nomor kendaraan", Fallback(vehicle, "Belum ditentukan"), last: true) +
        "</table></td></tr>" +
        "<tr><td align=\"center\" style=\"padding:18px 32px 28px\">" +
        "<div style=\"font-size:14px;font-weight:bold;color:#24324b\">QR Code Peserta</div>" +
        "<p style=\"font-size:13px;line-height:1.6;color:#64748b;margin:6px 0 14px\">Tunjukkan QR ini kepada panitia saat check-in dan check-out.</p>" +
        "<table role=\"presentation\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"margin:0 auto;background-color:#ffffff;border:1px solid #e3e9f2;border-radius:10px\"><tr><td style=\"padding:12px\">" +
        "<img src=\"cid:participantQr\" alt=\"QR Code peserta OMS\" width=\"220\" height=\"220\" style=\"display:block;width:220px;height:220px;border:0\">" +
        "</td></tr></table>" +
        "<p style=\"margin:22px 0 0\"><a href=\"" + Html(scheduleUrl) +
        "\" style=\"display:inline-block;padding:12px 20px;border-radius:7px;background-color:#2457a7;color:#ffffff;text-decoration:none;font-size:14px;font-weight:bold\">Lihat rundown kegiatan</a></p>" +
        "<p style=\"font-size:12px;color:#64748b;margin:10px 0 0\">Jadwal juga dapat dibuka dari tautan: <a href=\"" +
        Html(scheduleUrl) + "\" style=\"color:#2457a7\">" + Html(scheduleUrl) + "</a></p>" +
        "<p style=\"font-size:12px;color:#64748b;margin:16px 0 0\">QR Code ini bersifat pribadi. Jangan bagikan kepada orang lain.</p>" +
        "</td></tr>" +
        "<tr><td style=\"padding:18px 32px;background-color:#f7f9fc;border-top:1px solid #e7edf5;font-size:12px;line-height:1.6;color:#64748b\">" +
        "Salam hangat,<br><strong style=\"color:#24324b\">Panitia Outing</strong>" +
        "</td></tr></table></td></tr></table></body></html>";

    private static string DetailRow(string label, string value, bool last = false) =>
        $"<tr><td style=\"padding:12px 14px;{(last ? string.Empty : "border-bottom:1px solid #e7edf5;")}font-size:13px;color:#64748b;width:42%\">{Html(label)}</td>" +
        $"<td style=\"padding:12px 14px;{(last ? string.Empty : "border-bottom:1px solid #e7edf5;")}font-size:13px;font-weight:bold;color:#24324b\">{Html(value)}</td></tr>";

    private static string FormatDate(string date) =>
        DateTime.TryParse(date, out var parsed)
            ? parsed.ToString("dd MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("id-ID"))
            : "Akan diinformasikan";

    private static string Fallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
