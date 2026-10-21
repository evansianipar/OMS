function sendInvitationMail(eventId, participantId, token, qrDataUrl, camp, transport, vehicle) {
  requireRoles_([OMS.ROLES.SUPERADMIN, OMS.ROLES.ADMIN]);
  const event = requireEvent_(eventId);
  const participant = getRecordById_(OMS.SHEETS.PARTICIPANTS, 'ParticipantId', participantId);
  if (!participant || String(participant.EventId) !== String(eventId) || !isTrue_(participant.IsActive)) {
    throw new Error('Peserta tidak ditemukan atau tidak aktif pada kegiatan ini.');
  }
  if (!participant.Email || sha256_(String(token || '')) !== String(participant.QrTokenHash || '')) {
    throw new Error('Token QR tidak valid atau sudah diganti. Siapkan ulang undangan.');
  }
  if (!/^data:image\/png;base64,/.test(String(qrDataUrl || '')) || qrDataUrl.length > 150000) {
    throw new Error('Gambar QR tidak valid atau terlalu besar.');
  }
  if (MailApp.getRemainingDailyQuota() < 1) throw new Error('Kuota email harian Apps Script telah habis.');

  const image = Utilities.newBlob(
    Utilities.base64Decode(qrDataUrl.substring(qrDataUrl.indexOf(',') + 1)),
    'image/png',
    'qr-checkin.png'
  );
  const formattedDate = event.StartDate
    ? Utilities.formatDate(new Date(event.StartDate), OMS.TIME_ZONE, 'dd MMMM yyyy')
    : 'Akan diinformasikan';
  const location = String(event.Location || 'Akan diinformasikan');
  const campLabel = String(camp || 'Belum ditentukan');
  const transportLabel = String(transport || 'Belum ditentukan');
  const vehicleLabel = String(vehicle || 'Belum ditentukan');
  const subject = 'Undangan ' + String(event.EventName) + ' - QR Check-in dan Check-out';
  const htmlBody =
    '<div style="margin:0;padding:24px 12px;background:#f2f5fa;font-family:Arial,Helvetica,sans-serif;color:#172b4d">' +
      '<table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="border-collapse:collapse">' +
        '<tr><td align="center">' +
          '<table role="presentation" width="600" cellspacing="0" cellpadding="0" style="width:100%;max-width:600px;border-collapse:separate;border-spacing:0;background:#ffffff;border:1px solid #e3e9f2;border-radius:16px;overflow:hidden">' +
            '<tr><td style="padding:28px 32px;background:#14284b;color:#ffffff">' +
              '<div style="font-size:11px;font-weight:bold;letter-spacing:2px;color:#b9ccf4">OUTING MANAGEMENT SYSTEM</div>' +
              '<div style="padding-top:9px;font-size:24px;font-weight:bold;line-height:1.3">Undangan Kegiatan</div>' +
            '</td></tr>' +
            '<tr><td style="padding:28px 32px 12px">' +
              '<p style="margin:0 0 12px;font-size:15px;line-height:1.6">Yth. <strong>' + escapeHtml_(participant.FullName) + '</strong>,</p>' +
              '<p style="margin:0 0 22px;font-size:14px;line-height:1.7;color:#53627a">Anda terdaftar sebagai peserta pada kegiatan berikut.</p>' +
              '<table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="border-collapse:collapse;background:#f5f8fd;border:1px solid #e4eaf3;border-radius:10px">' +
                '<tr><td colspan="2" style="padding:16px 18px 8px;font-size:18px;font-weight:bold;color:#172b4d">' + escapeHtml_(event.EventName) + '</td></tr>' +
                '<tr><td style="padding:6px 18px 16px;font-size:13px;line-height:1.7;color:#53627a">' +
                  '<strong style="color:#172b4d">Tanggal</strong>&nbsp;&nbsp;' + escapeHtml_(formattedDate) +
                  '<br><strong style="color:#172b4d">Lokasi</strong>&nbsp;&nbsp;' + escapeHtml_(location) +
                '</td></tr>' +
              '</table>' +
              '<table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="margin-top:14px;border-collapse:collapse">' +
                '<tr><td style="padding:10px 14px;border-bottom:1px solid #e9edf4;font-size:12px;color:#718096">KODE PESERTA</td>' +
                    '<td align="right" style="padding:10px 14px;border-bottom:1px solid #e9edf4;font-size:13px;font-weight:bold;color:#172b4d">' + escapeHtml_(participant.EmployeeNumber) + '</td></tr>' +
                '<tr><td style="padding:10px 14px;font-size:12px;color:#718096">CAMP / NO. KAMAR</td>' +
                    '<td align="right" style="padding:10px 14px;font-size:13px;font-weight:bold;color:#172b4d">' + escapeHtml_(campLabel) + '</td></tr>' +
                '<tr><td style="padding:10px 14px;border-bottom:1px solid #e9edf4;font-size:12px;color:#718096">TRANSPORTASI</td>' +
                    '<td align="right" style="padding:10px 14px;border-bottom:1px solid #e9edf4;font-size:13px;font-weight:bold;color:#172b4d">' + escapeHtml_(transportLabel) + '</td></tr>' +
                '<tr><td style="padding:10px 14px;font-size:12px;color:#718096">NO. MOBIL</td>' +
                    '<td align="right" style="padding:10px 14px;font-size:13px;font-weight:bold;color:#172b4d">' + escapeHtml_(vehicleLabel) + '</td></tr>' +
              '</table>' +
            '</td></tr>' +
            '<tr><td align="center" style="padding:18px 32px 8px">' +
              '<div style="font-size:14px;font-weight:bold;color:#172b4d">QR Code Check-in dan Check-out</div>' +
              '<p style="margin:6px 0 16px;font-size:12px;line-height:1.6;color:#718096">Gunakan QR yang sama untuk check-in dan check-out. Tunjukkan QR ini kepada panitia saat proses berlangsung dan jangan bagikan kepada orang lain.</p>' +
              '<table role="presentation" cellspacing="0" cellpadding="0" style="margin:0 auto;border-collapse:collapse;background:#ffffff;border:1px solid #e3e9f2;border-radius:12px">' +
                '<tr><td align="center" style="padding:12px"><img src="cid:participantQr" alt="QR Code check-in dan check-out" width="200" height="200" style="display:block;width:200px;height:200px;border:0"></td></tr>' +
              '</table>' +
            '</td></tr>' +
            '<tr><td style="padding:22px 32px 28px;font-size:12px;line-height:1.7;color:#718096">' +
              'Simpan email ini untuk check-in dan check-out. Jika QR hilang atau informasi kegiatan berubah, hubungi panitia.' +
              '<br><br>Salam,<br><strong style="color:#172b4d">Panitia outing</strong>' +
            '</td></tr>' +
          '</table>' +
        '</td></tr>' +
      '</table>' +
    '</div>';
  const textBody =
    'Undangan ' + event.EventName +
    '. Tanggal: ' + formattedDate +
    '. Lokasi: ' + location +
    '. Kode peserta: ' + participant.EmployeeNumber +
    '. CAMP / No. Kamar: ' + campLabel +
    '. Transportasi: ' + transportLabel +
    '. No. Mobil: ' + vehicleLabel +
    '. Gunakan QR Code yang sama untuk check-in dan check-out.';

  MailApp.sendEmail({
    to: String(participant.Email),
    subject: subject,
    body: textBody,
    htmlBody: htmlBody,
    inlineImages: { participantQr: image }
  });
  audit_(eventId, 'SEND_INVITATION', 'Participant', participantId, 'Undangan QR dikirim.');
  return { success: true };
}
