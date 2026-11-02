(() => {
    const eventId = new URLSearchParams(window.location.search).get('eventId');
    const apiBase = String(window.OMS_API_BASE_URL ?? '').replace(/\/+$/, '');
    const clock = document.getElementById('wib-clock');
    const updatedAt = document.getElementById('updated-at');
    const errorBanner = document.getElementById('page-error');
    const scheduleElement = document.getElementById('schedule-timeline');
    let serverTime = Date.now();
    let serverPerfTime = performance.now();
    let schedule = [];

    const timeFormatter = new Intl.DateTimeFormat('id-ID', {
        timeZone: 'Asia/Jakarta',
        hour: '2-digit',
        minute: '2-digit',
        hourCycle: 'h23'
    });
    const clockFormatter = new Intl.DateTimeFormat('id-ID', {
        timeZone: 'Asia/Jakarta',
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit',
        hourCycle: 'h23'
    });
    const dayFormatter = new Intl.DateTimeFormat('id-ID', {
        timeZone: 'Asia/Jakarta',
        weekday: 'long',
        day: 'numeric',
        month: 'long',
        year: 'numeric'
    });
    const dateFormatter = new Intl.DateTimeFormat('id-ID', {
        timeZone: 'Asia/Jakarta',
        day: 'numeric',
        month: 'long',
        year: 'numeric'
    });

    function currentTime() {
        return serverTime + performance.now() - serverPerfTime;
    }

    function showError(message) {
        errorBanner.textContent = message;
        errorBanner.hidden = false;
    }

    function dateValue(value) {
        const parsed = new Date(value);
        return Number.isNaN(parsed.getTime()) ? null : parsed;
    }

    function activityIcon(name) {
        const value = name.toLocaleLowerCase('id-ID');
        if (value.includes('makan') || value.includes('sarapan') || value.includes('ishoma')) return '♨';
        if (value.includes('jalan') || value.includes('keberangkatan')) return '➤';
        if (value.includes('game') || value.includes('team')) return '✦';
        return '◈';
    }

    function renderSchedule(data) {
        const event = data.event ?? {};
        const items = (data.items ?? [])
            .map(item => ({ ...item, start: dateValue(item.startTime), end: dateValue(item.endTime) }))
            .filter(item => item.start && item.end)
            .sort((left, right) => left.start - right.start);

        document.title = `Rundown ${event.eventName ?? 'Kegiatan'} - OMS`;
        document.getElementById('event-name').textContent = event.eventName || 'Rundown kegiatan';
        const startDate = dateValue(event.startDate);
        const endDate = dateValue(event.endDate);
        document.getElementById('event-dates').textContent = startDate
            ? `◷ ${dateFormatter.format(startDate)}${endDate && dateFormatter.format(endDate) !== dateFormatter.format(startDate) ? ` — ${dateFormatter.format(endDate)}` : ''}`
            : '';
        document.getElementById('event-location').textContent = event.location ? `⌖ ${event.location}` : '';

        scheduleElement.replaceChildren();
        if (!items.length) {
            const empty = document.createElement('div');
            empty.className = 'empty-schedule';
            const title = document.createElement('h3');
            title.textContent = 'Rundown sedang disiapkan';
            const description = document.createElement('p');
            description.textContent = 'Jadwal kegiatan akan tampil di halaman ini setelah diperbarui panitia.';
            empty.append(title, description);
            scheduleElement.append(empty);
        } else {
            const days = new Map();
            for (const item of items) {
                const dayKey = new Intl.DateTimeFormat('en-CA', {
                    timeZone: 'Asia/Jakarta',
                    year: 'numeric',
                    month: '2-digit',
                    day: '2-digit'
                }).format(item.start);
                if (!days.has(dayKey)) {
                    const day = document.createElement('section');
                    day.className = 'timeline-day';
                    const heading = document.createElement('h3');
                    heading.className = 'day-heading';
                    const marker = document.createElement('span');
                    marker.className = 'day-marker';
                    heading.append(marker, document.createTextNode(dayFormatter.format(item.start)));
                    const list = document.createElement('div');
                    list.className = 'day-items';
                    day.append(heading, list);
                    days.set(dayKey, list);
                    scheduleElement.append(day);
                }

                const article = document.createElement('article');
                article.className = 'schedule-item';
                article.dataset.sessionId = item.sessionId;
                article.dataset.startMs = String(item.start.getTime());
                article.dataset.endMs = String(item.end.getTime());
                const rail = document.createElement('span');
                rail.className = 'timeline-rail';
                const icon = document.createElement('span');
                icon.className = 'timeline-icon';
                icon.setAttribute('aria-hidden', 'true');
                icon.textContent = activityIcon(item.sessionName ?? '');
                rail.append(icon);

                const card = document.createElement('div');
                card.className = 'activity-card';
                const topline = document.createElement('div');
                topline.className = 'activity-topline';
                const time = document.createElement('span');
                time.className = 'activity-time';
                time.append(
                    document.createTextNode(`${timeFormatter.format(item.start)} `),
                    document.createElement('b'),
                    document.createTextNode(` ${timeFormatter.format(item.end)} `)
                );
                time.querySelector('b').textContent = '—';
                const zone = document.createElement('small');
                zone.textContent = 'WIB';
                time.append(zone);
                const state = document.createElement('span');
                state.className = 'activity-state';
                topline.append(time, state);

                const name = document.createElement('h3');
                name.textContent = item.sessionName ?? 'Agenda kegiatan';
                card.append(topline, name);
                if (item.description) {
                    const description = document.createElement('p');
                    description.className = 'activity-description';
                    description.textContent = item.description;
                    card.append(description);
                }
                if (item.location) {
                    const location = document.createElement('p');
                    location.className = 'activity-location';
                    location.textContent = `⌖ ${item.location}`;
                    card.append(location);
                }
                article.append(rail, card);
                days.get(dayKey).append(article);
            }
        }

        schedule = [...document.querySelectorAll('.schedule-item')].map(element => ({
            start: Number(element.dataset.startMs),
            end: Number(element.dataset.endMs),
            element
        }));
        const apiTime = dateValue(data.currentTimeWib);
        if (apiTime) {
            serverTime = apiTime.getTime();
            serverPerfTime = performance.now();
        }
        errorBanner.hidden = true;
        updateTimeline();
    }

    function updateTimeline() {
        const now = currentTime();
        const current = schedule.find(item => item.start <= now && item.end > now);
        const next = schedule.find(item => item.start > now);
        for (const item of schedule) {
            const active = item === current;
            const done = item.end <= now;
            item.element.classList.toggle('is-current', active);
            item.element.classList.toggle('is-done', done);
            const status = item.element.querySelector('.activity-state');
            if (status) status.textContent = active ? 'SEDANG BERLANGSUNG' : done ? 'SELESAI' : 'AKAN DATANG';
        }

        clock.textContent = `${clockFormatter.format(new Date(now))} WIB`;
        const liveBadge = document.querySelector('#live-badge span');
        const label = document.getElementById('now-label');
        const activity = document.getElementById('now-activity');
        const detail = document.getElementById('now-detail');
        const icon = document.getElementById('now-icon');
        if (current) {
            label.textContent = 'SEDANG BERLANGSUNG';
            activity.textContent = current.element.querySelector('h3')?.textContent ?? 'Agenda kegiatan';
            detail.textContent = `Berlangsung hingga ${timeFormatter.format(new Date(current.end))} WIB`;
            icon.textContent = '✦';
            liveBadge.textContent = 'SEDANG BERLANGSUNG';
        } else if (next) {
            label.textContent = 'AGENDA SELANJUTNYA';
            activity.textContent = next.element.querySelector('h3')?.textContent ?? 'Agenda kegiatan';
            detail.textContent = `Dimulai pukul ${timeFormatter.format(new Date(next.start))} WIB`;
            icon.textContent = '◷';
            liveBadge.textContent = 'LIVE WIB';
        } else if (schedule.length) {
            label.textContent = 'RUNDOWN SELESAI';
            activity.textContent = 'Terima kasih telah mengikuti kegiatan';
            detail.textContent = 'Seluruh agenda kegiatan telah selesai.';
            icon.textContent = '✓';
            liveBadge.textContent = 'LIVE WIB';
        } else {
            label.textContent = 'JADWAL SAAT INI';
            activity.textContent = 'Rundown belum tersedia';
            detail.textContent = 'Panitia akan memperbarui jadwal kegiatan ini.';
            liveBadge.textContent = 'LIVE WIB';
        }
    }

    async function loadParticipantQr() {
        const match = /^#participantQr=([A-Za-z0-9_-]+)$/.exec(window.location.hash);
        if (!match) return;

        const token = match[1];
        window.history.replaceState(null, document.title, `${window.location.pathname}${window.location.search}`);
        const card = document.getElementById('participant-qr-card');
        const error = document.getElementById('participant-qr-error');
        const image = document.getElementById('participant-qr-image');
        card.hidden = false;
        if (!eventId || !apiBase) {
            error.textContent = 'Halaman rundown belum dikonfigurasi untuk memvalidasi QR. Hubungi panitia.';
            error.hidden = false;
            image.hidden = true;
            return;
        }

        try {
            const response = await fetch(`${apiBase}/api/public/events/${encodeURIComponent(eventId)}/participant-qr`, {
                method: 'POST',
                cache: 'no-store',
                headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
                body: JSON.stringify({ token })
            });
            if (!response.ok)
                throw new Error('QR peserta tidak aktif. Minta panitia mengirim ulang undangan terbaru.');

            const participant = await response.json();
            document.getElementById('participant-name').textContent = participant.fullName;
            document.getElementById('participant-number').textContent = participant.employeeNumber;
            image.src = `data:image/png;base64,${participant.qrPngBase64}`;
            image.hidden = false;
        } catch {
            error.textContent = 'QR tidak dapat dimuat. Periksa koneksi atau minta panitia mengirim ulang undangan terbaru.';
            error.hidden = false;
            document.getElementById('participant-name').textContent = 'QR tidak tersedia';
            document.getElementById('participant-number').textContent = '';
            image.hidden = true;
        }
    }

    async function refreshSchedule() {
        if (!eventId) {
            showError('ID kegiatan tidak ditemukan pada tautan. Buka rundown dari tautan pada email undangan.');
            document.getElementById('now-activity').textContent = 'Rundown tidak ditemukan';
            return;
        }
        if (!apiBase) {
            showError('Alamat API OMS belum diatur pada file config.js.');
            return;
        }
        try {
            const response = await fetch(`${apiBase}/api/public/events/${encodeURIComponent(eventId)}/schedule`, {
                cache: 'no-store',
                headers: { Accept: 'application/json' }
            });
            if (response.status === 404) throw new Error('Kegiatan tidak ditemukan.');
            if (!response.ok) throw new Error(`Jadwal tidak dapat dimuat (HTTP ${response.status}).`);
            const data = await response.json();
            renderSchedule(data);
            updatedAt.textContent = `Sinkronisasi terakhir ${clockFormatter.format(new Date(currentTime()))} WIB`;
        } catch (error) {
            showError(`${error.message} Jika masalah berlanjut, hubungi panitia.`);
            updatedAt.textContent = 'Jadwal belum tersinkron';
        }
    }

    window.addEventListener('hashchange', loadParticipantQr);
    loadParticipantQr();
    refreshSchedule();
    window.setInterval(updateTimeline, 1000);
    window.setInterval(refreshSchedule, 30000);
})();
