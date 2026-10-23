(() => {
    const page = document.querySelector('.schedule-page');
    const eventId = page?.dataset.eventId;
    const clock = document.getElementById('wib-clock');
    const nowLabel = document.getElementById('now-label');
    const nowActivity = document.getElementById('now-activity');
    const nowDetail = document.getElementById('now-detail');
    const nowIcon = document.getElementById('now-icon');
    const liveBadge = document.querySelector('#live-badge span');
    const updatedAt = document.getElementById('updated-at');
    let serverTime = Date.now();
    let serverPerfTime = performance.now();
    let schedule = [...document.querySelectorAll('.schedule-item')].map(item => ({
        id: item.dataset.sessionId,
        start: Number(item.dataset.startMs),
        end: Number(item.dataset.endMs),
        element: item
    }));

    const wibFormatter = new Intl.DateTimeFormat('id-ID', {
        timeZone: 'Asia/Jakarta',
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit',
        hourCycle: 'h23'
    });

    function currentTime() {
        return serverTime + (performance.now() - serverPerfTime);
    }

    function updateTimeline() {
        const now = currentTime();
        const current = schedule.find(item => item.start <= now && item.end > now);
        const next = schedule.find(item => item.start > now);

        schedule.forEach(item => {
            const active = item === current;
            const done = item.end <= now;
            item.element.classList.toggle('is-current', active);
            item.element.classList.toggle('is-done', done);
            const status = item.element.querySelector('.activity-state');
            if (status) status.textContent = active ? 'SEDANG BERLANGSUNG' : done ? 'SELESAI' : 'AKAN DATANG';
        });

        if (clock) clock.textContent = `${wibFormatter.format(new Date(now))} WIB`;
        if (liveBadge) liveBadge.textContent = current ? 'SEDANG BERLANGSUNG' : 'LIVE WIB';
        if (!nowActivity || !nowDetail || !nowLabel) return;
        if (current) {
            nowLabel.textContent = 'SEDANG BERLANGSUNG';
            nowActivity.textContent = current.element.querySelector('h3')?.textContent ?? 'Agenda kegiatan';
            nowDetail.textContent = `Berlangsung hingga ${new Intl.DateTimeFormat('id-ID', {
                timeZone: 'Asia/Jakarta', hour: '2-digit', minute: '2-digit', hourCycle: 'h23'
            }).format(new Date(current.end))} WIB`;
            if (nowIcon) nowIcon.textContent = '✦';
        } else if (next) {
            nowLabel.textContent = 'AGENDA SELANJUTNYA';
            nowActivity.textContent = next.element.querySelector('h3')?.textContent ?? 'Agenda kegiatan';
            nowDetail.textContent = `Dimulai pukul ${new Intl.DateTimeFormat('id-ID', {
                timeZone: 'Asia/Jakarta', hour: '2-digit', minute: '2-digit', hourCycle: 'h23'
            }).format(new Date(next.start))} WIB`;
            if (nowIcon) nowIcon.textContent = '◷';
        } else if (schedule.length) {
            nowLabel.textContent = 'RUNDOWN SELESAI';
            nowActivity.textContent = 'Terima kasih telah mengikuti kegiatan';
            nowDetail.textContent = 'Seluruh agenda kegiatan telah selesai.';
            if (nowIcon) nowIcon.textContent = '✓';
        }
    }

    async function loadParticipantQr() {
        const match = /^#participantQr=([A-Za-z0-9_-]+)$/.exec(window.location.hash);
        if (!match || !eventId) return;

        const token = match[1];
        window.history.replaceState(null, document.title, `${window.location.pathname}${window.location.search}`);
        const card = document.getElementById('participant-qr-card');
        const error = document.getElementById('participant-qr-error');
        card.hidden = false;

        try {
            const response = await fetch(`/api/public/events/${encodeURIComponent(eventId)}/participant-qr`, {
                method: 'POST',
                cache: 'no-store',
                headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
                body: JSON.stringify({ token })
            });
            if (!response.ok) throw new Error('QR peserta tidak aktif. Minta panitia mengirim ulang undangan terbaru.');

            const participant = await response.json();
            document.getElementById('participant-name').textContent = participant.fullName;
            document.getElementById('participant-number').textContent = participant.employeeNumber;
            document.getElementById('participant-qr-image').src =
                `data:image/png;base64,${participant.qrPngBase64}`;
        } catch (loadError) {
            error.hidden = false;
            error.textContent = loadError.message;
            document.getElementById('participant-name').textContent = 'QR tidak tersedia';
            document.getElementById('participant-number').textContent = '';
            document.getElementById('participant-qr-image').hidden = true;
        }
    }

    async function refreshSchedule() {
        if (!eventId) return;
        try {
            const response = await fetch(`/api/public/events/${encodeURIComponent(eventId)}/schedule`, {
                cache: 'no-store',
                headers: { Accept: 'application/json' }
            });
            if (!response.ok) throw new Error(`Jadwal tidak dapat diperbarui (HTTP ${response.status}).`);
            const data = await response.json();
            const incomingSignature = JSON.stringify((data.items ?? []).map(item => [
                item.sessionId, new Date(item.startTime).getTime(), new Date(item.endTime).getTime(),
                item.sessionName, item.description, item.location
            ]));
            const pageSignature = JSON.stringify(schedule.map(item => [
                item.id,
                item.start,
                item.end,
                item.element.dataset.name,
                item.element.dataset.description,
                item.element.dataset.location
            ]));
            if (incomingSignature !== pageSignature || data.event?.eventName !== document.querySelector('.event-hero h1')?.textContent)
            {
                window.location.reload();
                return;
            }
            serverTime = new Date(data.currentTimeWib).getTime();
            serverPerfTime = performance.now();
            if (updatedAt) updatedAt.textContent = `Sinkronisasi terakhir ${wibFormatter.format(new Date(serverTime))} WIB`;
        } catch (error) {
            if (updatedAt) updatedAt.textContent = error.message;
        }
        updateTimeline();
    }

    loadParticipantQr();
    refreshSchedule();
    window.setInterval(updateTimeline, 1000);
    window.setInterval(refreshSchedule, 30000);
})();
