// Injected into every Chrome Remote Desktop page hosted by Multi-Instance CRD.
// Keeps the page usable inside a multi-session shell: no browser fullscreen or keyboard lock
// (so the app's shortcuts keep working), connection state + stats for the top bar,
// pausing video in background sessions, and black (not grey) bars around the remote screen.
(function () {
    'use strict';
    if (window.__crdShell) return; // run once per document
    window.__crdShell = true;

    const post = (msg) => { try { window.chrome?.webview?.postMessage(msg); } catch (e) {} };

    // Sensible first-run default (scale to fit) without overriding a choice the user made in CRD.
    try {
        if (localStorage.getItem('remoting-shrink-to-fit') === null) {
            localStorage.setItem('remoting-shrink-to-fit', 'true');
        }
    } catch (e) {}

    // Background sessions are hidden WebViews. Report them as visible so CRD doesn't stall its
    // own heartbeat; video is paused explicitly via __crdPauseSession instead.
    try {
        Object.defineProperty(document, 'hidden', { get: () => false, configurable: true });
        Object.defineProperty(document, 'visibilityState', { get: () => 'visible', configurable: true });
    } catch (e) {}

    // Browser fullscreen and keyboard lock would hide the top bar and swallow the app's shortcuts.
    try {
        if (navigator.keyboard && navigator.keyboard.lock) {
            navigator.keyboard.lock = () => Promise.resolve();
        }
        const noop = function () { return Promise.resolve(); };
        if (Element.prototype.requestFullscreen) Element.prototype.requestFullscreen = noop;
        if (Element.prototype.webkitRequestFullscreen) Element.prototype.webkitRequestFullscreen = noop;
    } catch (e) {}

    // Track the session's peer connection to report connection state and stats.
    let activePeer = null;
    let reconnectAttempts = 0;
    try {
        const Original = window.RTCPeerConnection;
        if (Original) {
            const Wrapped = function (config, constraints) {
                const pc = new Original(config, constraints);
                activePeer = pc;
                post('CONN_STATE:' + pc.connectionState);
                pc.addEventListener('connectionstatechange', () => {
                    if (pc !== activePeer) return;
                    if (pc.connectionState === 'connected') reconnectAttempts = 0;
                    post('CONN_STATE:' + pc.connectionState);
                });
                return pc;
            };
            Wrapped.prototype = Original.prototype;
            Object.setPrototypeOf(Wrapped, Original);
            window.RTCPeerConnection = Wrapped;

            // close() does not fire connectionstatechange, so report it ourselves.
            const originalClose = Original.prototype.close;
            Original.prototype.close = function () {
                const result = originalClose.apply(this, arguments);
                if (this === activePeer) {
                    activePeer = null;
                    post('CONN_STATE:closed');
                }
                return result;
            };
        }
    } catch (e) {}

    // In split view, tell the app which pane the user is working in.
    const reportFocus = () => post('FOCUSED');
    window.addEventListener('pointerdown', reportFocus, true);
    window.addEventListener('focus', reportFocus, true);

    const stats = {
        timer: null,
        lastFrames: 0,
        lastTs: 0,
        start() {
            if (!this.timer) this.timer = setInterval(() => this.poll(), 2000);
        },
        stop() {
            if (this.timer) { clearInterval(this.timer); this.timer = null; }
            this.lastFrames = 0;
            this.lastTs = 0;
        },
        async poll() {
            const pc = activePeer;
            if (!pc || pc.connectionState !== 'connected') return;
            try {
                const report = await pc.getStats();
                let rtt = -1, fps = 0, width = 0, height = 0;
                report.forEach(r => {
                    if (r.type === 'candidate-pair' && r.nominated && r.state === 'succeeded' &&
                        r.currentRoundTripTime !== undefined) {
                        rtt = Math.round(r.currentRoundTripTime * 1000);
                    }
                    if (r.type === 'inbound-rtp' && r.kind === 'video') {
                        const frames = r.framesDecoded || 0;
                        if (this.lastTs > 0 && r.timestamp > this.lastTs) {
                            fps = Math.max(0, Math.round((frames - this.lastFrames) / ((r.timestamp - this.lastTs) / 1000)));
                        }
                        this.lastFrames = frames;
                        this.lastTs = r.timestamp;
                        width = r.frameWidth || 0;
                        height = r.frameHeight || 0;
                    }
                });
                if (rtt >= 0) post(JSON.stringify({ type: 'STATS', rtt, fps, width, height }));
            } catch (e) {}
        }
    };

    // Stop rendering video while the session is in the background. The connection stays up.
    // CRD attaches its stream only once a connection completes, so a session paused before that is
    // re-checked by the watchdog below (wantPaused) instead of being paused only once.
    let pausedStream = null;
    let wantPaused = false;
    function detachVideo() {
        try {
            const video = document.querySelector('video');
            if (video && video.srcObject) {
                pausedStream = video.srcObject;
                video.srcObject = null;
            }
        } catch (e) {}
    }

    window.__crdPauseSession = function () {
        wantPaused = true;
        stats.stop();
        detachVideo();
    };

    window.__crdResumeSession = async function () {
        wantPaused = false;
        try {
            const video = document.querySelector('video');
            if (pausedStream && video && !video.srcObject) {
                video.srcObject = pausedStream;
                try { await video.play(); } catch (e) {}
            }
            pausedStream = null;
            window.dispatchEvent(new Event('resize'));
        } catch (e) {}
        stats.start();
    };

    // CRD paints the area around the remote screen with a grey panel (class names are obfuscated and
    // change between releases). Find whatever large, opaque, neutral-grey layer sits at the edges of a
    // session view and make it black. The remote screen itself is a <canvas> and is never touched.
    function paintLetterbox() {
        if (!location.pathname.startsWith('/access/session/')) return;
        const w = innerWidth, h = innerHeight;
        const edges = [[w / 2, 1], [w / 2, h - 2], [1, h / 2], [w - 2, h / 2]];
        for (const [x, y] of edges) {
            for (const el of document.elementsFromPoint(x, y)) {
                if (el === document.documentElement || el === document.body || el.tagName === 'CANVAS' || el.tagName === 'VIDEO') continue;
                const r = el.getBoundingClientRect();
                if (r.width < w * 0.9 || r.height < h * 0.5) continue;
                const m = getComputedStyle(el).backgroundColor.match(/^rgba?\((\d+), (\d+), (\d+)(?:, ([\d.]+))?\)$/);
                if (!m || (m[4] !== undefined && +m[4] < 1) || m[1] !== m[2] || m[2] !== m[3] || m[1] === '0') continue;
                el.style.setProperty('background-color', '#000', 'important');
            }
        }
    }
    window.addEventListener('resize', () => setTimeout(paintLetterbox, 100));

    function injectStyles() {
        if (document.getElementById('crd-shell-styles')) return;
        const style = document.createElement('style');
        style.id = 'crd-shell-styles';
        style.textContent = `
            html, body { background: #000 !important; }
            video, canvas { background: #000 !important; }
        `;
        (document.head || document.documentElement).appendChild(style);
        stats.start();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', injectStyles, { once: true });
    } else {
        injectStyles();
    }

    // Click CRD's own Reconnect button after a drop: at most 5 tries, backing off from 5 s to 80 s.
    // The count only resets once a connection actually succeeds (see connectionstatechange above).
    let lastReconnect = 0;
    setInterval(() => {
        try {
            if (wantPaused) { detachVideo(); return; } // a stream that arrived after the pause
            paintLetterbox(); // CRD rebuilds its layout on connect and reconnect
            if (reconnectAttempts >= 5) return;
            const btn = document.querySelector('[data-qa-id="reconnect-button"], .reconnect-button');
            if (btn && btn.offsetParent !== null) {
                const now = Date.now();
                if (now - lastReconnect > 5000 * Math.pow(2, reconnectAttempts)) {
                    lastReconnect = now;
                    reconnectAttempts++;
                    btn.click();
                }
            }
        } catch (e) {}
    }, 2500);
})();
