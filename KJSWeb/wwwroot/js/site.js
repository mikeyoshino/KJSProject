document.addEventListener('DOMContentLoaded', function () {
    // Mobile hamburger toggle
    const toggle = document.getElementById('mobileToggle');
    const mobileNav = document.getElementById('mobileNav');

    if (toggle && mobileNav) {
        toggle.addEventListener('click', function () {
            toggle.classList.toggle('active');
            mobileNav.classList.toggle('active');
        });
    }

    // Mobile categories dropdown
    const catToggle = document.getElementById('mobileCatToggle');
    const catDropdown = document.getElementById('mobileCatDropdown');
    const catArrow = document.getElementById('mobileCatArrow');

    if (catToggle && catDropdown) {
        catToggle.addEventListener('click', function () {
            catDropdown.classList.toggle('active');
            if (catArrow) {
                catArrow.textContent = catDropdown.classList.contains('active') ? '▲' : '▼';
            }
        });
    }

    // Search overlay
    const overlay  = document.getElementById('search-overlay');
    const panel    = document.getElementById('searchPanel');
    const input    = document.getElementById('searchInput');
    const results  = document.getElementById('search-results');

    function openSearch() {
        if (!overlay) return;
        overlay.classList.remove('opacity-0', 'pointer-events-none');
        overlay.classList.add('opacity-100', 'pointer-events-auto');
        panel.classList.remove('translate-y-4');
        document.body.style.overflow = 'hidden';
        requestAnimationFrame(() => input && input.focus());
    }

    function closeSearch() {
        if (!overlay) return;
        overlay.classList.add('opacity-0', 'pointer-events-none');
        overlay.classList.remove('opacity-100', 'pointer-events-auto');
        panel.classList.add('translate-y-4');
        document.body.style.overflow = '';
        overlay.addEventListener('transitionend', function handler() {
            if (input) input.value = '';
            if (results) results.innerHTML = '';
            overlay.removeEventListener('transitionend', handler);
        });
    }

    // ── JGirl thumbnail scrubber ─────────────────────────────────
    let activeScrubCard = null;

    // WeakMap: card → Set<loadedIndex> for preload tracking
    const _cardLoaded = new WeakMap();

    function getCardFrames(card) {
        try { return JSON.parse(card.dataset.frames || '[]'); } catch { return []; }
    }

    function preloadCardFrames(card) {
        const frames = getCardFrames(card);
        if (!frames.length) return;
        if (!_cardLoaded.has(card)) _cardLoaded.set(card, new Set([0]));
        const loaded = _cardLoaded.get(card);
        frames.forEach((src, i) => {
            if (loaded.has(i)) return;
            const pi = new Image();
            pi.onload = pi.onerror = () => loaded.add(i);
            pi.src = src;
        });
    }

    function scrubberPct(card, clientX) {
        const bar  = card.querySelector('.scrubber-bar');
        if (!bar) return 0;
        const rect = bar.getBoundingClientRect();
        return Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
    }

    function applyScrubFrame(card, pct) {
        const frames = getCardFrames(card);
        if (!frames.length) return;

        const idx     = Math.min(frames.length - 1, Math.floor(pct * frames.length));
        const img     = card.querySelector('.scrubber-thumb');
        const fill    = card.querySelector('.scrubber-fill');
        const knob    = card.querySelector('.scrubber-knob');
        const counter = card.querySelector('.scrubber-counter');

        // Show nearest already-loaded frame so scrubbing is never blank
        let displayIdx = idx;
        const loaded = _cardLoaded.get(card);
        if (loaded && !loaded.has(idx)) {
            for (let d = 1; d < frames.length; d++) {
                if (idx - d >= 0 && loaded.has(idx - d)) { displayIdx = idx - d; break; }
                if (idx + d < frames.length && loaded.has(idx + d)) { displayIdx = idx + d; break; }
            }
        }

        if (img && img.src !== frames[displayIdx]) img.src = frames[displayIdx];
        if (fill)    fill.style.width      = `${pct * 100}%`;
        if (knob)    knob.style.left       = `${pct * 100}%`;
        if (counter) {
            counter.textContent  = `${idx + 1} / ${frames.length}`;
            counter.style.opacity = '1';
        }
    }

    function resetScrubber(card) {
        const counter = card.querySelector('.scrubber-counter');
        if (counter) counter.style.opacity = '0';
    }

    // Hover scrubbing (desktop)
    document.querySelectorAll('[data-frames]').forEach(card => {
        const img = card.querySelector('.scrubber-thumb');
        const imgWrap = img?.parentElement;
        if (imgWrap) {
            // Kick off background preload on first hover
            imgWrap.addEventListener('mouseenter', () => preloadCardFrames(card), { once: true });
            imgWrap.addEventListener('mousemove', e => {
                if (activeScrubCard !== card) {
                    const rect = imgWrap.getBoundingClientRect();
                    const pct  = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
                    applyScrubFrame(card, pct);
                }
            });
            imgWrap.addEventListener('mouseleave', () => resetScrubber(card));
        }

        const bar = card.querySelector('.scrubber-bar');
        if (bar) {
            bar.addEventListener('mousedown', e => {
                e.preventDefault();
                activeScrubCard = card;
                applyScrubFrame(card, scrubberPct(card, e.clientX));
            });
            bar.addEventListener('touchstart', e => {
                e.preventDefault();
                activeScrubCard = card;
                applyScrubFrame(card, scrubberPct(card, e.touches[0].clientX));
            }, { passive: false });
            bar.addEventListener('touchmove', e => {
                e.preventDefault();
                applyScrubFrame(card, scrubberPct(card, e.touches[0].clientX));
            }, { passive: false });
            bar.addEventListener('touchend', () => { activeScrubCard = null; });
        }
    });

    document.addEventListener('mousemove', e => {
        if (activeScrubCard) applyScrubFrame(activeScrubCard, scrubberPct(activeScrubCard, e.clientX));
    });
    document.addEventListener('mouseup', () => { activeScrubCard = null; });

    document.getElementById('searchOpen')?.addEventListener('click', openSearch);
    document.getElementById('searchOpenMobile')?.addEventListener('click', openSearch);
    document.getElementById('searchClose')?.addEventListener('click', closeSearch);
    document.getElementById('searchBackdrop')?.addEventListener('click', closeSearch);
    document.addEventListener('keydown', e => { if (e.key === 'Escape') closeSearch(); });
});

// Dark mode toggle
(function () {
    const toggle = document.getElementById('theme-toggle');
    const sunIcon = document.getElementById('icon-sun');
    const moonIcon = document.getElementById('icon-moon');

    function applyTheme(isDark) {
        document.documentElement.classList.toggle('dark', isDark);
        if (sunIcon) sunIcon.classList.toggle('hidden', !isDark);
        if (moonIcon) moonIcon.classList.toggle('hidden', isDark);
    }

    // Init icon state on load
    applyTheme(document.documentElement.classList.contains('dark'));

    if (toggle) {
        toggle.addEventListener('click', function () {
            const isDark = !document.documentElement.classList.contains('dark');
            applyTheme(isDark);
            localStorage.setItem('theme', isDark ? 'dark' : 'light');
        });
    }
})();
