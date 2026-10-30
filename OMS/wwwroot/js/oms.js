(() => {
    const state = { scanner: null, starting: false, scanning: false, requestInFlight: false, checkoutInFlight: false, checkoutCandidate: null, dashboardRefreshInFlight: false, participantPage: 1, reportPage: 1, participantQr: null, participantsById: new Map() };
    const csrf = document.querySelector('meta[name="csrf-token"]')?.content ?? '';
    const eventSelect = document.getElementById('event-select');
    const currentPage = document.querySelector('.app-shell')?.dataset.page ?? 'dashboard';
    const canManage = document.querySelector('.app-shell')?.dataset.canManage === 'true';
    const dashboardChannel = 'BroadcastChannel' in window ? new BroadcastChannel('oms-attendance') : null;
    const eventId = () => eventSelect?.value ?? '';

    function setMobileMenu(open) {
        const sidebar = document.querySelector('.sidebar');
        const toggle = document.querySelector('.menu-toggle');
        const backdrop = document.querySelector('.menu-backdrop');
        const wasOpen = sidebar?.classList.contains('is-open') ?? false;
        const isOpen = open && window.matchMedia('(max-width: 900px)').matches;
        sidebar?.classList.toggle('is-open', isOpen);
        backdrop?.classList.toggle('is-visible', isOpen);
        toggle?.setAttribute('aria-expanded', String(isOpen));
        toggle?.setAttribute('aria-label', isOpen ? 'Tutup menu' : 'Buka menu');
        sidebar?.setAttribute('aria-hidden', String(!isOpen && window.matchMedia('(max-width: 900px)').matches));
        if (isOpen && !wasOpen) sidebar?.querySelector('.nav-item')?.focus();
        else if (!isOpen && wasOpen) toggle?.focus();
    }

    document.querySelector('.menu-toggle')?.addEventListener('click', () => {
        const isOpen = document.querySelector('.menu-toggle')?.getAttribute('aria-expanded') === 'true';
        setMobileMenu(!isOpen);
    });
    document.querySelector('.menu-backdrop')?.addEventListener('click', () => setMobileMenu(false));
    document.querySelectorAll('.sidebar-nav .nav-item').forEach(link =>
        link.addEventListener('click', () => setMobileMenu(false)));
    document.addEventListener('keydown', event => {
        if (event.key === 'Escape') setMobileMenu(false);
    });
    const desktopViewport = window.matchMedia('(min-width: 901px)');
    if (desktopViewport.addEventListener) desktopViewport.addEventListener('change', () => setMobileMenu(false));
    else desktopViewport.addListener(() => setMobileMenu(false));
    setMobileMenu(false);

    const escapeHtml = (value) => String(value ?? '').replace(/[&<>"']/g, char => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[char]);

    function notify(message, type = 'success') {
        const container = document.getElementById('toast-container');
        const toast = document.createElement('div');
        toast.className = `toast show text-bg-${type === 'error' ? 'danger' : type === 'warning' ? 'warning' : 'success'} border-0`;
        toast.setAttribute('role', 'alert');
        toast.innerHTML = `<div class="d-flex"><div class="toast-body">${escapeHtml(message)}</div><button class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast"></button></div>`;
        container.appendChild(toast);
        setTimeout(() => toast.remove(), 5000);
    }

    async function api(path, options = {}) {
        const response = await fetch(path, {
            credentials: 'same-origin',
            ...options,
            headers: {
                'Content-Type': 'application/json',
                'X-CSRF-TOKEN': csrf,
                ...(options.headers ?? {})
            }
        });
        if (response.status === 401 || response.status === 403) {
            throw new Error('Sesi login berakhir atau akses Anda dicabut. Muat ulang dan masuk kembali');
        }
        const payload = await response.json().catch(() => ({}));
        if (!response.ok) throw new Error(payload.detail || payload.title || 'Permintaan ke server gagal');
        return payload;
    }

    function openScanner() {
        navigateToPage('scanner');
    }

    async function closeScanner() {
        await stopScanner();
        navigateToPage('dashboard');
    }

    async function openParticipants() {
        navigateToPage('participants');
    }

    function closeParticipants() {
        navigateToPage('dashboard');
    }

    function navigateToPage(page) {
        const url = new URL(window.location.href);
        url.searchParams.set('page', page);
        if (eventId()) url.searchParams.set('eventId', eventId());
        window.location.assign(url);
    }

    async function loadSessions() {
        try {
            const sessions = await api(`/api/oms/sessions?eventId=${encodeURIComponent(eventId())}`);
            const select = document.getElementById('session-select');
            select.innerHTML = '<option value="">Check-in umum kegiatan</option>' +
                sessions.filter(item => item.status.toLowerCase() === 'active')
                    .map(item => `<option value="${escapeHtml(item.sessionId)}">${escapeHtml(item.sessionName)}</option>`).join('');
        } catch (error) { setCameraMessage(error.message, true); }
    }

    function setCameraMessage(message, isError = false, isSuccess = false) {
        const element = document.getElementById('scan-message');
        element.textContent = message;
        element.classList.toggle('error', isError);
        element.classList.toggle('success', isSuccess);
    }

    function setCheckoutCandidate(result, method = 'QR') {
        const panel = document.getElementById('checkout-confirmation');
        const button = document.getElementById('confirm-checkout');
        const details = document.getElementById('checkout-participant');
        if (!panel || !button || !details) return;

        if (result?.status !== 'DUPLICATE' || result.checkOutTime || !result.participant) {
            state.checkoutCandidate = null;
            panel.classList.add('d-none');
            button.disabled = false;
            return;
        }

        state.checkoutCandidate = {
            participantId: result.participant.participantId,
            method,
            attendanceTime: result.attendanceTime,
            eventId: eventId(),
            sessionId: document.getElementById('session-select')?.value || null
        };
        details.innerHTML = `${escapeHtml(result.participant.fullName)} · ${escapeHtml(result.participant.employeeNumber)} · ${escapeHtml(result.participant.department)}<br>` +
            `Check-in: ${escapeHtml(formatDate(result.attendanceTime))}. Pastikan identitas peserta benar`;
        panel.classList.remove('d-none');
        button.disabled = false;
    }

    async function confirmCheckout() {
        if (!state.checkoutCandidate || state.checkoutInFlight) return;
        const button = document.getElementById('confirm-checkout');
        state.checkoutInFlight = true;
        button.disabled = true;
        try {
            const result = await api('/api/oms/scanner/check-out', {
                method: 'POST',
                body: JSON.stringify({
                    eventId: state.checkoutCandidate.eventId,
                    sessionId: state.checkoutCandidate.sessionId,
                    participantId: state.checkoutCandidate.participantId,
                    method: state.checkoutCandidate.method
                })
            });
            setCheckoutCandidate(null);
            displayResult(result);
            if (result.status === 'CHECK_OUT_SUCCESS') {
                dashboardChannel?.postMessage({ eventId: eventId() });
                if (currentPage === 'dashboard') refreshDashboard(true);
            }
        } catch (error) {
            displayResult({ status: 'ERROR', message: error.message });
            button.disabled = false;
        } finally {
            state.checkoutInFlight = false;
        }
    }

    document.getElementById('confirm-checkout')?.addEventListener('click', confirmCheckout);
    document.getElementById('session-select')?.addEventListener('change', () => {
        setCheckoutCandidate(null);
        document.getElementById('inline-scan-result')?.remove();
    });

    async function startScanner() {
        if (state.starting || state.scanning) return;
        if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) {
            setCameraMessage('Live camera memerlukan HTTPS (atau localhost) dan browser yang mendukung getUserMedia', true);
            return;
        }
        if (typeof Html5Qrcode === 'undefined') {
            setCameraMessage('Library scanner gagal dimuat. Periksa koneksi/CDN dan muat ulang halaman', true);
            return;
        }

        state.starting = true;
        const startButton = document.getElementById('start-scan');
        startButton.disabled = true;
        setCameraMessage('Meminta izin dan menyiapkan kamera');
        let permissionStream;
        try {
            permissionStream = await navigator.mediaDevices.getUserMedia({
                video: { facingMode: { ideal: 'environment' } },
                audio: false
            });
            permissionStream.getTracks().forEach(track => track.stop());
            permissionStream = null;
            const cameras = await Html5Qrcode.getCameras();
            if (!cameras.length) throw new DOMException('Tidak ada kamera ditemukan', 'NotFoundError');
            const camera = cameras.find(item => /back|rear|environment|belakang/i.test(item.label)) ?? cameras[0];
            state.scanner = new Html5Qrcode('qr-reader');
            await state.scanner.start(
                { deviceId: { exact: camera.id } },
                { fps: 10, qrbox: { width: 240, height: 240 } },
                token => processQr(token),
                () => {}
            );
            state.scanning = true;
            document.getElementById('stop-scan').disabled = false;
            setCameraMessage(`Kamera aktif: ${camera.label || 'kamera perangkat'}. Arahkan QR pada bingkai`, false, true);
        } catch (error) {
            permissionStream?.getTracks().forEach(track => track.stop());
            if (state.scanner) {
                try { state.scanner.clear(); } catch { /* Scanner may not have completed initialization. */ }
            }
            state.scanner = null;
            setCameraMessage(cameraError(error), true);
        } finally {
            state.starting = false;
            startButton.disabled = false;
        }
    }

    function cameraError(error) {
        const name = error?.name ?? '';
        if (name === 'NotAllowedError' || name === 'PermissionDeniedError')
            return 'Izin kamera ditolak. Izinkan kamera untuk domain aplikasi OMS, muat ulang halaman, lalu coba lagi';
        if (name === 'NotFoundError' || name === 'DevicesNotFoundError')
            return 'Tidak ada kamera yang ditemukan pada perangkat';
        if (name === 'NotReadableError' || name === 'TrackStartError')
            return 'Kamera sedang digunakan aplikasi/tab lain atau diblokir sistem operasi. Tutup aplikasi kamera lain lalu coba kembali';
        if (name === 'OverconstrainedError' || name === 'ConstraintNotSatisfiedError')
            return 'Kamera tidak mendukung mode yang diminta. Coba kamera lain atau gunakan QR dari foto';
        return `Kamera gagal dimulai (${name || 'Error'}): ${error?.message || 'Kesalahan tidak diketahui'}`;
    }

    async function stopScanner() {
        if (state.scanner && state.scanning) {
            try {
                await state.scanner.stop();
                state.scanner.clear();
            } catch (error) {
                setCameraMessage(`Scanner gagal dihentikan: ${error.message}`, true);
            }
        }
        state.scanning = false;
        state.scanner = null;
        const stopButton = document.getElementById('stop-scan');
        if (stopButton) stopButton.disabled = true;
    }

    async function scanImage(file) {
        if (!file) return;
        if (file.size > 10 * 1024 * 1024) return notify('Ukuran gambar maksimal 10 MB', 'error');
        if (state.scanning || state.starting) return notify('Hentikan scanner kamera sebelum memilih gambar', 'warning');
        if (typeof Html5Qrcode === 'undefined') return notify('Library QR belum termuat', 'error');
        const reader = new Html5Qrcode('qr-reader');
        setCameraMessage('Membaca QR dari gambar');
        try {
            const token = await reader.scanFile(file, true);
            await processQr(token);
        } catch (error) {
            setCameraMessage(`QR tidak terbaca pada gambar. Pastikan kode fokus dan terlihat penuh. ${error.message || ''}`, true);
        } finally {
            try { reader.clear(); } catch { /* No scanner instance was started. */ }
            document.getElementById('qr-reader').innerHTML = '';
        }
    }

    async function processQr(token) {
        if (state.requestInFlight) return;
        state.requestInFlight = true;
        try {
            const result = await api('/api/oms/scanner/check-in', {
                method: 'POST',
                body: JSON.stringify({
                    eventId: eventId(),
                    sessionId: document.getElementById('session-select').value || null,
                    token
                })
            });
            displayResult(result);
            if (result.status === 'SUCCESS') {
                dashboardChannel?.postMessage({ eventId: eventId() });
                if (currentPage === 'dashboard') refreshDashboard(true);
            }
        } catch (error) {
            displayResult({ status: 'ERROR', message: error.message });
        } finally {
            setTimeout(() => { state.requestInFlight = false; }, 1000);
        }
    }

    function displayResult(result, method = 'QR') {
        const area = document.getElementById('scan-result-area');
        const id = area ? 'inline-scan-result' : 'scan-result-toast';
        document.getElementById(id)?.remove();
        const container = area ?? document.getElementById('toast-container');
        const box = document.createElement('div');
        const status = {
            SUCCESS: { alert: 'alert-success', title: 'Check-in berhasil' },
            CHECK_OUT_SUCCESS: { alert: 'alert-success', title: 'Check-out berhasil' },
            DUPLICATE: {
                alert: 'alert-warning',
                title: result.checkOutTime ? 'Peserta sudah check-in dan check-out' : 'Peserta sudah check-in'
            },
            ALREADY_CHECKED_OUT: { alert: 'alert-warning', title: 'Peserta sudah check-out' },
            CHECKIN_REQUIRED: { alert: 'alert-danger', title: 'Check-in belum tercatat' }
        }[result.status] ?? { alert: 'alert-danger', title: 'Check-in gagal' };
        setCheckoutCandidate(result, method);
        box.id = id;
        box.className = `alert ${status.alert} scan-alert d-flex align-items-start gap-3${area ? ' scan-result-inline' : ''}`;
        const successful = result.status === 'SUCCESS' || result.status === 'CHECK_OUT_SUCCESS';
        box.setAttribute('role', successful ? 'status' : 'alert');
        const participant = result.participant;
        const timeLabel = result.status === 'CHECK_OUT_SUCCESS' || result.status === 'ALREADY_CHECKED_OUT' ||
            (result.status === 'DUPLICATE' && result.checkOutTime)
            ? 'Waktu check-out'
            : result.status === 'DUPLICATE' ? 'Waktu check-in' : 'Waktu';
        const checkmark = successful
            ? '<span class="checkmark-icon" aria-hidden="true"><svg viewBox="0 0 24 24" focusable="false"><path d="m5 12.5 4.5 4.5L19 7"></path></svg></span>'
            : '';
        box.innerHTML = `${checkmark}<div><strong>${status.title}</strong><div>${escapeHtml(result.message)}</div>` +
            (participant ? `<small>${escapeHtml(participant.fullName)} · ${escapeHtml(participant.employeeNumber)} · ${escapeHtml(participant.department)}<br>${timeLabel}: ${escapeHtml(formatDate(result.checkOutTime || result.attendanceTime))}</small>` : '') +
            '</div>';
        container.appendChild(box);
        if (successful || result.status === 'DUPLICATE' || result.status === 'ALREADY_CHECKED_OUT')
            setTimeout(() => box.remove(), 3000);
    }

    let searchTimer;
    async function searchParticipants() {
        state.participantPage = 1;
        await loadParticipants();
    }

    async function searchManualParticipants() {
        const query = document.getElementById('manual-search')?.value.trim() ?? '';
        const results = document.getElementById('manual-results');
        if (!results) return;
        if (query.length < 2) {
            results.innerHTML = '<span class="text-secondary small">Masukkan minimal 2 karakter NIK atau nama</span>';
            return;
        }
        try {
            const participants = await api(`/api/oms/participants/search?eventId=${encodeURIComponent(eventId())}&query=${encodeURIComponent(query)}`);
            results.innerHTML = participants.map(participant =>
                `<div class="manual-result"><div><strong>${escapeHtml(participant.fullName)}</strong><br><span>${escapeHtml(participant.employeeNumber)} · ${escapeHtml(participant.department || '-')}</span></div><button class="btn btn-sm btn-primary" data-manual-checkin="${escapeHtml(participant.participantId)}">Check-in</button></div>`
            ).join('') || '<span class="text-secondary small">Peserta tidak ditemukan. Periksa NIK dan kegiatan terpilih.</span>';
            results.querySelectorAll('[data-manual-checkin]').forEach(button =>
                button.addEventListener('click', () => manualCheckIn(button.dataset.manualCheckin)));
        } catch (error) { notify(error.message, 'error'); }
    }

    async function loadParticipants() {
        if (currentPage !== 'participants') return;
        try {
            const query = document.getElementById('participant-search').value.trim();
            const pageData = await api(`/api/oms/participants?eventId=${encodeURIComponent(eventId())}&query=${encodeURIComponent(query)}&page=${state.participantPage}&pageSize=25`);
            const pageCount = Math.max(1, Math.ceil(pageData.total / pageData.pageSize));
            document.getElementById('participant-page').textContent = `${pageData.total} peserta ditemukan`;
            document.getElementById('participant-page-control').textContent = `Halaman ${pageData.page} dari ${pageCount}`;
            document.getElementById('participant-prev').disabled = pageData.page <= 1;
            document.getElementById('participant-next').disabled = pageData.page >= pageCount;
            state.participantsById = new Map(pageData.items.map(participant => [participant.participantId, participant]));
            document.getElementById('participant-table').innerHTML = pageData.items.map(participant =>
                `<tr><td>${escapeHtml(participant.employeeNumber)}</td><td>${escapeHtml(participant.fullName)}</td><td>${escapeHtml(participant.employeeType || '-')}</td><td>${escapeHtml(participant.positionName || '-')}</td><td>${escapeHtml(participant.department || '-')}</td><td>${escapeHtml(participant.gender || '-')}</td><td>${escapeHtml(participant.email || '-')}</td><td>${escapeHtml(participant.camp || '-')}</td><td>${escapeHtml(participant.transport || '-')}</td><td>${escapeHtml(participant.vehicle || '-')}</td><td>${escapeHtml(participant.groupNumber || '-')}</td><td><span class="badge ${participant.isActive ? 'text-bg-success' : 'text-bg-secondary'}">${participant.isActive ? 'Aktif' : 'Nonaktif'}</span></td><td>${canManage ? `<div class="d-flex flex-wrap gap-2">${participant.isActive ? `<button class="btn btn-sm btn-outline-primary" data-qr-participant="${escapeHtml(participant.participantId)}">Buat QR</button><button class="btn btn-sm btn-primary" data-send-invitation="${escapeHtml(participant.participantId)}">Kirim undangan</button>` : ''}<button class="btn btn-sm btn-outline-secondary" data-edit-participant="${escapeHtml(participant.participantId)}">Edit</button><button class="btn btn-sm btn-outline-danger" data-delete-participant="${escapeHtml(participant.participantId)}" data-participant-name="${escapeHtml(participant.fullName)}">Hapus</button></div>` : '-'}</td></tr>`
            ).join('') || '<tr><td colspan="13" class="text-secondary">Tidak ada peserta.</td></tr>';
            document.querySelectorAll('[data-qr-participant]').forEach(button =>
                button.addEventListener('click', () => issueParticipantQr(button.dataset.qrParticipant)));
            document.querySelectorAll('[data-send-invitation]').forEach(button =>
                button.addEventListener('click', () => sendIndividualInvitation(button)));
            document.querySelectorAll('[data-edit-participant]').forEach(button =>
                button.addEventListener('click', () => editParticipant(button.dataset.editParticipant)));
            document.querySelectorAll('[data-delete-participant]').forEach(button =>
                button.addEventListener('click', () => deleteParticipant(button)));
        } catch (error) { notify(error.message, 'error'); }
    }

    function showParticipantForm(participant = null) {
        const form = document.getElementById('participant-form');
        const panel = document.getElementById('participant-form-panel');
        if (!form || !panel) return;
        form.reset();
        document.getElementById('participant-edit-id').value = participant?.participantId ?? '';
        document.getElementById('participant-employee-number').value = participant?.employeeNumber ?? '';
        document.getElementById('participant-full-name').value = participant?.fullName ?? '';
        document.getElementById('participant-employee-type').value = participant?.employeeType ?? '';
        document.getElementById('participant-position').value = participant?.positionName ?? '';
        document.getElementById('participant-department').value = participant?.department ?? '';
        document.getElementById('participant-gender').value = participant?.gender ?? '';
        document.getElementById('participant-email').value = participant?.email ?? '';
        document.getElementById('participant-camp').value = participant?.camp ?? '';
        document.getElementById('participant-transport').value = participant?.transport ?? '';
        document.getElementById('participant-vehicle').value = participant?.vehicle ?? '';
        document.getElementById('participant-group-number').value = participant?.groupNumber ?? '';
        document.getElementById('participant-status').value = String(participant?.isActive ?? true);
        document.getElementById('participant-form-title').textContent = participant ? 'Edit data peserta' : 'Tambah peserta';
        document.getElementById('participant-save').textContent = participant ? 'Simpan perubahan' : 'Simpan peserta';
        panel.classList.remove('d-none');
        document.getElementById('participant-employee-number').focus();
        panel.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }

    function editParticipant(participantId) {
        const participant = state.participantsById.get(participantId);
        if (!participant) return notify('Data peserta tidak ditemukan pada halaman ini. Muat ulang daftar peserta.', 'error');
        showParticipantForm(participant);
    }

    async function saveParticipant(event) {
        event.preventDefault();
        const participantId = document.getElementById('participant-edit-id').value;
        const request = {
            employeeNumber: document.getElementById('participant-employee-number').value.trim(),
            fullName: document.getElementById('participant-full-name').value.trim(),
            employeeType: document.getElementById('participant-employee-type').value.trim(),
            positionName: document.getElementById('participant-position').value.trim(),
            department: document.getElementById('participant-department').value.trim(),
            gender: document.getElementById('participant-gender').value.trim(),
            email: document.getElementById('participant-email').value.trim(),
            camp: document.getElementById('participant-camp').value.trim(),
            transport: document.getElementById('participant-transport').value.trim(),
            vehicle: document.getElementById('participant-vehicle').value.trim(),
            groupNumber: document.getElementById('participant-group-number').value.trim(),
            isActive: document.getElementById('participant-status').value === 'true'
        };
        const button = document.getElementById('participant-save');
        button.disabled = true;
        try {
            const path = participantId
                ? `/api/oms/participants/${encodeURIComponent(participantId)}?eventId=${encodeURIComponent(eventId())}`
                : `/api/oms/participants?eventId=${encodeURIComponent(eventId())}`;
            await api(path, {
                method: participantId ? 'PUT' : 'POST',
                body: JSON.stringify(request)
            });
            document.getElementById('participant-form-panel').classList.add('d-none');
            document.getElementById('participant-form').reset();
            document.getElementById('participant-edit-id').value = '';
            state.participantPage = 1;
            await loadParticipants();
            notify(participantId ? 'Perubahan data peserta berhasil disimpan.' : 'Peserta berhasil ditambahkan.');
        } catch (error) {
            notify(error.message, 'error');
        } finally {
            button.disabled = false;
        }
    }

    async function deleteParticipant(button) {
        const participantId = button.dataset.deleteParticipant;
        const participantName = button.dataset.participantName || 'peserta ini';
        if (!confirm(`Hapus permanen ${participantName} dari kegiatan ini? Riwayat kehadiran, QR, dan log peserta terkait juga akan dihapus.`))
            return;

        button.disabled = true;
        try {
            await api(`/api/oms/participants/${encodeURIComponent(participantId)}?eventId=${encodeURIComponent(eventId())}`, {
                method: 'DELETE'
            });
            state.participantPage = 1;
            await loadParticipants();
            notify('Peserta dan riwayat kehadirannya berhasil dihapus.');
        } catch (error) {
            button.disabled = false;
            notify(error.message, 'error');
        }
    }

    async function deleteAllParticipants() {
        const selectedEvent = eventSelect?.selectedOptions[0]?.textContent?.trim() || 'kegiatan terpilih';
        if (!eventId()) return notify('Pilih kegiatan terlebih dahulu.', 'warning');
        if (!confirm(
            `Hapus SEMUA peserta dari ${selectedEvent}?\n\n` +
            'Tindakan ini permanen dan juga menghapus catatan kehadiran serta audit terkait peserta. ' +
            'Master karyawan CSV dan kegiatan tidak dihapus.'))
            return;

        const button = document.getElementById('delete-all-participants');
        button.disabled = true;
        try {
            const result = await api(`/api/oms/events/${encodeURIComponent(eventId())}/participants`, {
                method: 'DELETE'
            });
            state.participantPage = 1;
            document.getElementById('qr-output')?.classList.add('d-none');
            await loadParticipants();
            notify(
                `${result.participants} peserta dihapus; ${result.attendanceRecords} catatan kehadiran dan ` +
                `${result.auditRecords} audit peserta terkait turut dihapus.`);
        } catch (error) {
            notify(error.message, 'error');
        } finally {
            button.disabled = false;
        }
    }

    async function issueParticipantQr(participantId) {
        if (typeof QRCode === 'undefined') return notify('Library QR Code belum termuat', 'error');
        if (!confirm('QR baru akan membatalkan QR lama peserta ini. Lanjutkan?')) return;
        try {
            const result = await api('/api/oms/participants/issue-qr', {
                method: 'POST',
                body: JSON.stringify({ eventId: eventId(), participantId })
            });
            const target = document.getElementById('qr-code');
            target.innerHTML = '';
            document.getElementById('qr-output').classList.remove('d-none');
            document.getElementById('qr-caption').textContent = `${result.fullName} · ${result.employeeNumber}`;
            new QRCode(target, { text: result.token, width: 220, height: 220, correctLevel: QRCode.CorrectLevel.M });
            state.participantQr = { employeeNumber: result.employeeNumber, fullName: result.fullName };
            document.getElementById('download-qr').onclick = () => {
                const image = target.querySelector('canvas')?.toDataURL('image/png') || target.querySelector('img')?.src;
                if (!image) return notify('QR belum siap diunduh', 'error');
                const anchor = document.createElement('a');
                anchor.href = image;
                anchor.download = `QR-${state.participantQr.employeeNumber}.png`;
                anchor.click();
            };
            notify('QR diterbitkan. QR sebelumnya untuk peserta ini sudah tidak berlaku');
        } catch (error) { notify(error.message, 'error'); }
    }

    async function importMaster() {
        if (!eventId()) return notify('Pilih kegiatan terlebih dahulu', 'warning');
        if (!confirm('Impor karyawan baru dari master CSV yang sudah diunggah? Nomor pegawai yang sudah terdaftar tidak akan diduplikasi')) return;
        try {
            const result = await api('/api/oms/participants/import-master', {
                method: 'POST',
                body: JSON.stringify({ eventId: eventId() })
            });
            notify(`Impor selesai: ${result.imported} baru, ${result.existing} sudah terdaftar, ${result.invalid} tidak valid`);
            await loadParticipants();
        } catch (error) { notify(error.message, 'error'); }
    }

    function parseCsv(text) {
        const rows = [];
        let row = [];
        let field = '';
        let quoted = false;
        for (let index = 0; index < text.length; index++) {
            const character = text[index];
            if (quoted) {
                if (character === '"' && text[index + 1] === '"') {
                    field += '"';
                    index++;
                } else if (character === '"') {
                    quoted = false;
                } else {
                    field += character;
                }
            } else if (character === '"' && field.length === 0) {
                quoted = true;
            } else if (character === ',') {
                row.push(field);
                field = '';
            } else if (character === '\n' || character === '\r') {
                if (character === '\r' && text[index + 1] === '\n') index++;
                row.push(field);
                if (row.some(cell => cell.trim() !== '')) rows.push(row);
                row = [];
                field = '';
            } else {
                field += character;
            }
        }
        row.push(field);
        if (row.some(cell => cell.trim() !== '')) rows.push(row);
        return rows;
    }

    async function uploadEmployeeMaster(file) {
        try {
            const rows = parseCsv(await file.text());
            if (rows.length < 2) throw new Error('CSV harus berisi header dan minimal satu baris data.');
            const headers = rows.shift().map((header, index) =>
                index === 0 ? header.replace(/^\uFEFF/, '').trim() : header.trim());
            const result = await api('/api/oms/employee-master', {
                method: 'POST',
                body: JSON.stringify({ headers, rows })
            });
            notify(`Master karyawan tersimpan dalam database JSON: ${result.imported} baris.`);
        } catch (error) {
            notify(error.message, 'error');
        } finally {
            document.getElementById('employee-master-file').value = '';
        }
    }

    async function createEvent() {
        const editingEventId = document.getElementById('editing-event-id').value;
        const request = {
            eventCode: document.getElementById('new-event-code').value,
            eventName: document.getElementById('new-event-name').value,
            status: document.getElementById('new-event-status').value,
            startDate: document.getElementById('new-event-start').value,
            endDate: document.getElementById('new-event-end').value,
            location: document.getElementById('new-event-location').value,
            description: document.getElementById('new-event-description').value
        };
        const button = document.getElementById('create-event-submit');
        const status = document.getElementById('create-event-status');
        button.disabled = true;
        button.textContent = 'Menyimpan...';
        status.className = 'alert alert-info mt-3 mb-0';
        status.textContent = editingEventId ? 'Menyimpan perubahan kegiatan...' : 'Menyimpan kegiatan, tunggu sebentar...';
        try {
            const result = await api(
                editingEventId ? `/api/oms/events/${encodeURIComponent(editingEventId)}` : '/api/oms/events',
                { method: editingEventId ? 'PUT' : 'POST', body: JSON.stringify(request) });
            const url = new URL(window.location.href);
            url.searchParams.set('eventId', result.eventId);
            url.searchParams.set('page', 'data');
            window.location.assign(url);
        } catch (error) {
            status.className = 'alert alert-danger mt-3 mb-0';
            status.textContent = error.message;
            button.disabled = false;
            button.textContent = 'Simpan kegiatan';
        }
    }

    function startEventEdit(button) {
        document.getElementById('editing-event-id').value = button.dataset.editEvent;
        document.getElementById('new-event-code').value = button.dataset.code || '';
        document.getElementById('new-event-name').value = button.dataset.name || '';
        document.getElementById('new-event-description').value = button.dataset.description || '';
        document.getElementById('new-event-start').value = (button.dataset.startDate || '').slice(0, 10);
        document.getElementById('new-event-end').value = (button.dataset.endDate || '').slice(0, 10);
        document.getElementById('new-event-location').value = button.dataset.location || '';
        document.getElementById('new-event-status').value = button.dataset.status || 'Draft';
        document.getElementById('event-form-title').textContent = 'Edit kegiatan';
        document.getElementById('create-event-submit').textContent = 'Simpan perubahan';
        document.getElementById('cancel-event-edit').classList.remove('d-none');
        document.getElementById('create-event-status').className = 'alert d-none mt-3 mb-0';
        document.getElementById('new-event-form').scrollIntoView({ behavior: 'smooth', block: 'start' });
        document.getElementById('new-event-code').focus();
    }

    function cancelEventEdit() {
        document.getElementById('create-event-form').reset();
        document.getElementById('editing-event-id').value = '';
        document.getElementById('event-form-title').textContent = 'Buat kegiatan';
        document.getElementById('create-event-submit').textContent = 'Simpan kegiatan';
        document.getElementById('cancel-event-edit').classList.add('d-none');
        document.getElementById('create-event-status').className = 'alert d-none mt-3 mb-0';
    }

    async function deleteEvent(button) {
        const eventRow = button.closest('tr');
        const eventName = eventRow?.querySelector('td:nth-child(2)')?.textContent?.trim() || 'kegiatan ini';
        if (!confirm(`Hapus permanen kegiatan "${eventName}" beserta semua peserta, sesi, data kehadiran, dan log terkait? Tindakan ini tidak dapat dibatalkan.`))
            return;

        button.disabled = true;
        try {
            await api(`/api/oms/events/${encodeURIComponent(button.dataset.deleteEvent)}`, {
                method: 'DELETE'
            });
            window.location.assign('/?page=data');
        } catch (error) {
            button.disabled = false;
            notify(error.message, 'error');
        }
    }

    function editScheduleItem(button) {
        document.getElementById('schedule-session-id').value = button.dataset.editSchedule;
        document.getElementById('schedule-name').value = button.dataset.name || '';
        document.getElementById('schedule-description').value = button.dataset.description || '';
        document.getElementById('schedule-start').value = (button.dataset.start || '').slice(0, 16);
        document.getElementById('schedule-end').value = (button.dataset.end || '').slice(0, 16);
        document.getElementById('schedule-location').value = button.dataset.location || '';
        document.getElementById('schedule-status').value = button.dataset.status || 'Active';
        document.getElementById('schedule-save').textContent = 'Simpan perubahan';
        document.getElementById('schedule-cancel').classList.remove('d-none');
        document.getElementById('schedule-form-status').className = 'alert d-none mt-3 mb-0';
        document.getElementById('schedule-management').scrollIntoView({ behavior: 'smooth', block: 'start' });
        document.getElementById('schedule-name').focus();
    }

    function resetScheduleForm() {
        document.getElementById('schedule-item-form').reset();
        document.getElementById('schedule-session-id').value = '';
        document.getElementById('schedule-save').textContent = 'Tambah agenda';
        document.getElementById('schedule-cancel').classList.add('d-none');
        document.getElementById('schedule-form-status').className = 'alert d-none mt-3 mb-0';
    }

    async function saveScheduleItem() {
        const sessionId = document.getElementById('schedule-session-id').value;
        const status = document.getElementById('schedule-form-status');
        const button = document.getElementById('schedule-save');
        const payload = {
            sessionName: document.getElementById('schedule-name').value,
            description: document.getElementById('schedule-description').value,
            startTime: document.getElementById('schedule-start').value,
            endTime: document.getElementById('schedule-end').value,
            location: document.getElementById('schedule-location').value,
            status: document.getElementById('schedule-status').value
        };
        if (!eventId()) return notify('Pilih kegiatan terlebih dahulu.', 'warning');

        button.disabled = true;
        status.className = 'alert alert-info mt-3 mb-0';
        status.textContent = 'Menyimpan agenda...';
        try {
            await api(
                sessionId
                    ? `/api/oms/events/${encodeURIComponent(eventId())}/schedule/${encodeURIComponent(sessionId)}`
                    : `/api/oms/events/${encodeURIComponent(eventId())}/schedule`,
                { method: sessionId ? 'PUT' : 'POST', body: JSON.stringify(payload) });
            window.location.reload();
        } catch (error) {
            status.className = 'alert alert-danger mt-3 mb-0';
            status.textContent = error.message;
        } finally {
            button.disabled = false;
        }
    }

    async function deleteScheduleItem(button) {
        if (!confirm('Hapus agenda ini dari rundown? Jika agenda sudah memiliki catatan kehadiran, agenda tidak dapat dihapus; gunakan status Sembunyikan.'))
            return;
        button.disabled = true;
        try {
            await api(
                `/api/oms/events/${encodeURIComponent(eventId())}/schedule/${encodeURIComponent(button.dataset.deleteSchedule)}`,
                { method: 'DELETE' });
            window.location.reload();
        } catch (error) {
            button.disabled = false;
            notify(error.message, 'error');
        }
    }

    async function manualCheckIn(participantId) {
        try {
            const result = await api('/api/oms/scanner/manual-check-in', {
                method: 'POST',
                body: JSON.stringify({
                    eventId: eventId(),
                    sessionId: document.getElementById('session-select').value || null,
                    participantId
                })
            });
            displayResult(result, 'MANUAL');
            if (result.status === 'SUCCESS') {
                dashboardChannel?.postMessage({ eventId: eventId() });
                if (currentPage === 'dashboard') refreshDashboard(true);
            }
        } catch (error) { displayResult({ status: 'ERROR', message: error.message }); }
    }

    async function sendInvitations() {
        if (!eventId()) return notify('Pilih kegiatan terlebih dahulu', 'warning');
        if (!confirm('Kirim email undangan QR kepada semua peserta aktif yang memiliki email valid? QR akan diterbitkan ulang dan QR lama tidak berlaku. QR yang baru dapat digunakan untuk check-in dan check-out.')) return;
        const button = document.getElementById('send-invitations');
        const progress = document.getElementById('invite-progress');
        button.disabled = true;
        progress.classList.remove('d-none');
        progress.className = 'alert alert-info';
        progress.textContent = 'Sedang membuat QR dan mengirim undangan. Tunggu hingga proses selesai';
        try {
            let offset = 0;
            let total = 0;
            let sent = 0;
            let failed = 0;
            let skipped = 0;
            const errors = [];
            do {
                const result = await api('/api/oms/participants/send-invitations', {
                    method: 'POST',
                    body: JSON.stringify({ eventId: eventId(), offset, batchSize: 10 })
                });
                total = result.total;
                sent += result.sent;
                failed += result.failed;
                skipped += result.skipped;
                errors.push(...(result.errors ?? []));
                offset += result.processed;
                progress.textContent = total
                    ? `Mengirim undangan: ${Math.min(offset, total)} dari ${total} diproses · ${sent} terkirim · ${failed} gagal`
                    : `Tidak ada peserta aktif dengan alamat email valid. ${skipped} peserta dilewati`;
                if (result.processed === 0) break;
            } while (offset < total);
            progress.className = `alert ${failed ? 'alert-warning' : 'alert-success'}`;
            progress.textContent = `Selesai: ${sent} terkirim, ${failed} gagal, ${skipped} dilewati karena email kosong/tidak valid`;
            if (errors.length) notify(errors.slice(0, 3).join(' | '), 'error');
        } catch (error) {
            progress.className = 'alert alert-danger';
            progress.textContent = error.message;
        } finally {
            button.disabled = false;
        }
    }

    async function sendIndividualInvitation(button) {
        if (!eventId()) return notify('Pilih kegiatan terlebih dahulu', 'warning');
        const participantId = button.dataset.sendInvitation;
        if (!participantId || !confirm('Kirim ulang undangan kepada peserta ini? QR akan diterbitkan ulang dan QR lama peserta ini tidak berlaku.')) return;

        button.disabled = true;
        const originalText = button.textContent;
        button.textContent = 'Mengirim...';
        try {
            await api('/api/oms/participants/send-invitation', {
                method: 'POST',
                body: JSON.stringify({ eventId: eventId(), participantId })
            });
            notify('Undangan berhasil dikirim. QR yang baru dapat digunakan untuk check-in dan check-out');
        } catch (error) {
            notify(error.message, 'error');
        } finally {
            button.disabled = false;
            button.textContent = originalText;
        }
    }

    async function loadAttendanceReport() {
        if (currentPage !== 'data' || !eventId() || !document.getElementById('attendance-report-body')) return;
        try {
            const data = await api(`/api/oms/reports/attendance?eventId=${encodeURIComponent(eventId())}&page=${state.reportPage}&pageSize=50`);
            const pages = Math.max(1, Math.ceil(data.total / data.pageSize));
            document.getElementById('report-page-label').textContent =
                `${data.total} peserta dalam laporan kegiatan terpilih`;
            document.getElementById('report-page-control').textContent = `Halaman ${data.page} dari ${pages}`;
            document.getElementById('report-prev').disabled = data.page <= 1;
            document.getElementById('report-next').disabled = data.page >= pages;
            document.getElementById('attendance-report-body').innerHTML = data.items.map(item =>
                `<tr><td>${escapeHtml(item.employeeNumber)}</td><td>${escapeHtml(item.fullName)}</td><td>${escapeHtml(item.department || '-')}</td><td><span class="badge ${item.status === 'Hadir' ? 'text-bg-success' : 'text-bg-light'}">${escapeHtml(item.status)}</span></td><td>${escapeHtml(formatDate(item.attendanceTime))}</td><td>${escapeHtml(formatDate(item.checkOutTime))}</td><td>${escapeHtml([item.method, item.checkOutMethod].filter(Boolean).join(' / ') || '-')}</td></tr>`
            ).join('') || '<tr><td colspan="7" class="text-secondary">Belum ada peserta pada kegiatan ini.</td></tr>';
        } catch (error) {
            document.getElementById('attendance-report-body').innerHTML =
                `<tr><td colspan="7" class="text-danger">${escapeHtml(error.message)}</td></tr>`;
        }
    }

    async function refreshDashboard(showError = false) {
        if (state.dashboardRefreshInFlight || !document.getElementById('stat-total')) return;
        state.dashboardRefreshInFlight = true;
        const syncStatus = document.getElementById('dashboard-sync-status');
        try {
            const data = await api(`/api/oms/dashboard?eventId=${encodeURIComponent(eventId())}`);
            updateDashboardStat('stat-total', data.total);
            updateDashboardStat('stat-attended', data.attended);
            updateDashboardStat('stat-absent', data.absent);
            updateDashboardStat('stat-percent', `${data.attendancePercent}%`);
            document.getElementById('attendance-progress').style.width = `${data.attendancePercent}%`;
            document.getElementById('department-body').innerHTML = data.departments.map(item =>
                `<tr><td>${escapeHtml(item.department)}</td><td>${item.total}</td><td>${item.attended}</td><td><div class="progress"><div class="progress-bar" style="width:${item.total ? item.attended * 100 / item.total : 0}%"></div></div></td></tr>`
            ).join('') || '<tr><td colspan="4" class="text-secondary">Belum ada data kehadiran</td></tr>';
            document.getElementById('recent-list').innerHTML = data.recent.map(item =>
                `<div class="activity-item"><span class="activity-dot"></span><div><strong>${escapeHtml(item.fullName)}</strong><small>${escapeHtml(item.employeeNumber)} · ${escapeHtml(item.department)} · ${escapeHtml(formatDate(item.attendanceTime))}</small></div></div>`
            ).join('') || '<span class="small text-secondary">Belum ada check-in.</span>';
            document.getElementById('absent-list').innerHTML = data.absentParticipants.map(item =>
                `<span class="absent-chip">${escapeHtml(item.fullName)} · ${escapeHtml(item.employeeNumber)}</span>`
            ).join('') || '<span class="small text-secondary">Semua peserta sudah check-in</span>';
            if (syncStatus) {
                syncStatus.textContent = `Diperbarui otomatis pukul ${new Date().toLocaleTimeString('id-ID', { hour: '2-digit', minute: '2-digit' })}`;
                syncStatus.removeAttribute('title');
            }
        } catch (error) {
            if (syncStatus) {
                syncStatus.textContent = 'Pembaruan otomatis tertunda';
                syncStatus.title = error.message;
            }
            if (showError) notify(error.message, 'error');
        } finally {
            state.dashboardRefreshInFlight = false;
        }
    }

    function updateDashboardStat(id, value) {
        const element = document.getElementById(id);
        const nextValue = String(value);
        if (element.textContent === nextValue) return;
        element.textContent = nextValue;
        element.classList.remove('stat-updated');
        void element.offsetWidth;
        element.classList.add('stat-updated');
        element.addEventListener('animationend', () => element.classList.remove('stat-updated'), { once: true });
    }

    function formatDate(value) {
        const localTimestamp = /^(\d{2})\/(\d{2})\/(\d{4}) (\d{2}):(\d{2}):(\d{2})$/.exec(String(value ?? ''));
        if (localTimestamp)
            return `${localTimestamp[1]}/${localTimestamp[2]}/${localTimestamp[3]} ${localTimestamp[4]}:${localTimestamp[5]}:${localTimestamp[6]}`;
        const date = new Date(value);
        return value && !Number.isNaN(date.valueOf()) ? date.toLocaleString('id-ID') : '-';
    }

    eventSelect?.addEventListener('change', () => {
        const url = new URL(window.location.href);
        url.searchParams.set('eventId', eventId());
        window.location.assign(url);
    });
    dashboardChannel?.addEventListener('message', message => {
        if (currentPage === 'dashboard' && message.data?.eventId === eventId())
            refreshDashboard();
    });
    document.getElementById('participant-search')?.addEventListener('input', () => {
        clearTimeout(searchTimer);
        searchTimer = setTimeout(searchParticipants, 250);
    });
    document.getElementById('participant-prev')?.addEventListener('click', () => {
        state.participantPage = Math.max(1, state.participantPage - 1);
        loadParticipants();
    });
    document.getElementById('participant-next')?.addEventListener('click', () => {
        state.participantPage++;
        loadParticipants();
    });
    document.getElementById('delete-all-participants')?.addEventListener('click', deleteAllParticipants);
    document.getElementById('add-participant')?.addEventListener('click', () => showParticipantForm());
    document.getElementById('participant-form')?.addEventListener('submit', saveParticipant);
    document.getElementById('participant-cancel')?.addEventListener('click', () => {
        document.getElementById('participant-form-panel')?.classList.add('d-none');
        document.getElementById('participant-form')?.reset();
        document.getElementById('participant-edit-id').value = '';
    });
    document.getElementById('manual-search')?.addEventListener('input', () => {
        clearTimeout(searchTimer);
        searchTimer = setTimeout(searchManualParticipants, 250);
    });
    document.getElementById('manual-search-button')?.addEventListener('click', searchManualParticipants);
    document.getElementById('manual-search')?.addEventListener('keydown', event => {
        if (event.key === 'Enter') {
            event.preventDefault();
            searchManualParticipants();
        }
    });
    document.getElementById('report-prev')?.addEventListener('click', () => {
        state.reportPage = Math.max(1, state.reportPage - 1);
        loadAttendanceReport();
    });
    document.getElementById('report-next')?.addEventListener('click', () => {
        state.reportPage++;
        loadAttendanceReport();
    });
    if (currentPage === 'dashboard') {
        window.setInterval(() => {
            if (!document.hidden) refreshDashboard();
        }, 15000);
    } else if (currentPage === 'scanner') {
        loadSessions();
        document.getElementById('manual-search')?.focus();
    } else if (currentPage === 'participants') {
        loadParticipants();
    } else if (currentPage === 'data') {
        loadAttendanceReport();
    }
    window.addEventListener('beforeunload', () => {
        if (state.scanner && state.scanning) state.scanner.stop().catch(() => {});
        dashboardChannel?.close();
    });

    window.openScanner = openScanner;
    window.closeScanner = closeScanner;
    window.openParticipants = openParticipants;
    window.closeParticipants = closeParticipants;
    window.createEvent = createEvent;
    window.startScanner = startScanner;
    window.stopScanner = stopScanner;
    window.scanImage = scanImage;
    window.importMaster = importMaster;
    document.getElementById('create-event-form')?.addEventListener('submit', event => {
        event.preventDefault();
        createEvent();
    });
    document.getElementById('cancel-event-edit')?.addEventListener('click', cancelEventEdit);
    document.querySelectorAll('[data-edit-event]').forEach(button =>
        button.addEventListener('click', () => startEventEdit(button)));
    document.querySelectorAll('[data-delete-event]').forEach(button =>
        button.addEventListener('click', () => deleteEvent(button)));
    document.getElementById('schedule-item-form')?.addEventListener('submit', event => {
        event.preventDefault();
        saveScheduleItem();
    });
    document.getElementById('schedule-cancel')?.addEventListener('click', resetScheduleForm);
    document.querySelectorAll('[data-edit-schedule]').forEach(button =>
        button.addEventListener('click', () => editScheduleItem(button)));
    document.querySelectorAll('[data-delete-schedule]').forEach(button =>
        button.addEventListener('click', () => deleteScheduleItem(button)));
    document.getElementById('employee-master-file')?.addEventListener('change', event => {
        const file = event.target.files?.[0];
        if (file) uploadEmployeeMaster(file);
    });
    window.sendInvitations = sendInvitations;
})();
