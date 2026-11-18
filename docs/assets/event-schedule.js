(() => {
    const eventId = new URLSearchParams(window.location.search).get('eventId');
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

    function appendTextWithLinks(element, text) {
        const urlPattern = /https?:\/\/[^\s<>"']+/gi;
        let cursor = 0;
        for (const match of text.matchAll(urlPattern)) {
            const rawUrl = match[0];
            let url = rawUrl;
            while (/[.,!?;:)\]}]$/.test(url)) url = url.slice(0, -1);
            if (!url) continue;

            const start = match.index;
            element.append(document.createTextNode(text.slice(cursor, start)));
            try {
                const parsed = new URL(url);
                if (parsed.protocol !== 'https:' && parsed.protocol !== 'http:')
                    throw new Error('Unsupported URL protocol');
                const link = document.createElement('a');
                link.href = parsed.href;
                link.target = '_blank';
                link.rel = 'noopener noreferrer';
                link.textContent = url;
                link.setAttribute('aria-label', `Buka tautan ${url}`);
                element.append(link);
            } catch {
                element.append(document.createTextNode(url));
            }
            if (url.length < rawUrl.length)
                element.append(document.createTextNode(rawUrl.slice(url.length)));
            cursor = start + rawUrl.length;
        }
        element.append(document.createTextNode(text.slice(cursor)));
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
            ? `◷ ${dateFormatter.format(startDate)}${endDate && dateFormatter.format(endDate) !== dateFormatter.format(startDate) ? ` - ${dateFormatter.format(endDate)}` : ''}`
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
                time.querySelector('b').textContent = '-';
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
                    appendTextWithLinks(description, item.description);
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
        const exportedAt = dateValue(data.exportedAtWib);
        errorBanner.hidden = true;
        updateTimeline();
        updatedAt.textContent = exportedAt
            ? `Data terakhir diekspor ${dateFormatter.format(exportedAt)} ${clockFormatter.format(exportedAt)} WIB`
            : 'Rundown statis dari file JSON GitHub Pages';
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

    function loadParticipantQr() {
        const match = /^#participantQr=([A-Za-z0-9_-]+)$/.exec(window.location.hash);
        if (!match) return;

        const token = match[1];
        window.history.replaceState(null, document.title, `${window.location.pathname}${window.location.search}`);
        const card = document.getElementById('participant-qr-card');
        const error = document.getElementById('participant-qr-error');
        const image = document.getElementById('participant-qr-image');
        card.hidden = false;

        try {
            const qr = qrcode(0, 'Q');
            qr.addData(token);
            qr.make();
            image.src = qr.createDataURL(8, 4);
            image.hidden = false;
            error.hidden = true;
        } catch {
            error.textContent = 'QR tidak dapat dibuat dari tautan ini. Minta panitia mengirim ulang undangan.';
            error.hidden = false;
            image.hidden = true;
        }
    }

    async function refreshSchedule() {
        if (!eventId) {
            showError('ID kegiatan tidak ditemukan pada tautan. Buka rundown dari tautan pada email undangan.');
            document.getElementById('now-activity').textContent = 'Rundown tidak ditemukan';
            return;
        }
        if (!/^[A-Za-z0-9_-]+$/.test(eventId)) {
            showError('ID kegiatan pada tautan tidak valid.');
            return;
        }
        try {
            const scheduleUrl = new URL(
                `./data/rundown/rundown-${encodeURIComponent(eventId)}.json`,
                window.location.href);
            const response = await fetch(scheduleUrl, {
                cache: 'no-cache',
                headers: { Accept: 'application/json' }
            });
            if (response.status === 404)
                throw new Error('File JSON rundown belum tersedia di GitHub Pages. Ekspor ulang dari OMS dan ganti file rundown untuk kegiatan ini.');
            if (!response.ok) throw new Error(`File rundown tidak dapat dimuat (HTTP ${response.status}).`);
            const data = await response.json();
            renderSchedule(data);
        } catch (error) {
            showError(`${error.message} Jika masalah berlanjut, hubungi panitia.`);
            updatedAt.textContent = 'File JSON rundown belum dimuat';
        }
    }

    window.addEventListener('hashchange', loadParticipantQr);
    loadParticipantQr();
    refreshSchedule();
    window.setInterval(updateTimeline, 1000);
})();
