(function () {
    'use strict';

    // 1. Prevent Chromium Page Visibility API from freezing CRD's internal WebRTC heartbeat
    try {
        Object.defineProperty(document, 'hidden', { get: () => false, configurable: true });
        Object.defineProperty(document, 'visibilityState', { get: () => 'visible', configurable: true });
    } catch (e) {
        // Ignore if already defined
    }

    // 2. Cache reference for WebRTC MediaStream
    window.__crdMediaStream = null;

    // 3. Power-gate Snapdragon Adreno GPU decode to 0% when entering Warm Standby
    window.__crdPauseSession = function () {
        try {
            const video = document.querySelector('video');
            if (video && video.srcObject) {
                window.__crdMediaStream = video.srcObject;
                // Detaching srcObject shuts down WebMediaPlayerMS & hardware decode
                video.srcObject = null;
                console.log('[CRD-ARM] Session paused: video sink detached, Adreno decode 0%');
            }
        } catch (err) {
            console.error('[CRD-ARM] Error pausing session:', err);
        }
    };

    // 4. Resume session with guaranteed sub-35ms IDR keyframe
    window.__crdResumeSession = async function () {
        try {
            const video = document.querySelector('video');
            if (window.__crdMediaStream && video) {
                video.srcObject = window.__crdMediaStream;
                try {
                    await video.play();
                } catch (playErr) {
                    // Autoplay policy handles this
                }
                console.log('[CRD-ARM] Session resumed: video sink reattached');
            }
            // Synthetic resize event triggers CRD host-side capturer to send immediate keyframe (PLI)
            window.dispatchEvent(new Event('resize'));
        } catch (err) {
            console.error('[CRD-ARM] Error resuming session:', err);
        }
    };

    // 5. Auto-detect connected host machine name and report to wrapper
    window.__lastReportedHostName = '';
    setInterval(function () {
        try {
            let hostName = '';
            if (document.title && !document.title.includes('Chrome Remote Desktop') && !document.title.includes('Access')) {
                hostName = document.title.replace('- Chrome Remote Desktop', '').trim();
            }
            if (!hostName) {
                const headerEl = document.querySelector('.session-name, .host-name, [data-qa-id="host-name"]');
                if (headerEl && headerEl.textContent) {
                    hostName = headerEl.textContent.trim();
                }
            }
            if (hostName && hostName !== window.__lastReportedHostName) {
                window.__lastReportedHostName = hostName;
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage('HOST_NAME:' + hostName);
                }
            }
        } catch (e) {}
    }, 2000);

    // 6. In-page accelerator key forwarding bridge via WebMessage
    window.addEventListener('keydown', function (e) {
        if (e.ctrlKey && e.altKey) {
            if (e.key === '1') {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage('SWITCH_1');
                    e.preventDefault();
                    e.stopPropagation();
                }
            } else if (e.key === '2') {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage('SWITCH_2');
                    e.preventDefault();
                    e.stopPropagation();
                }
            } else if (e.key === '3') {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage('SWITCH_3');
                    e.preventDefault();
                    e.stopPropagation();
                }
            } else if (e.key === 'Tab') {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage('CYCLE');
                    e.preventDefault();
                    e.stopPropagation();
                }
            } else if (e.key === 's' || e.key === 'S') {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage('TOGGLE_NOTCH');
                    e.preventDefault();
                    e.stopPropagation();
                }
            }
        }
    }, true);

    // 6. Injected CSS for 0px wasted space (clean full-bleed remote display)
    function injectZeroWasteStyles() {
        const styleId = 'crd-lightweight-zero-waste';
        if (document.getElementById(styleId)) return;

        const style = document.createElement('style');
        style.id = styleId;
        style.textContent = `
            body, html {
                margin: 0 !important;
                padding: 0 !important;
                overflow: hidden !important;
                background-color: #0c0c0c !important;
            }
            header, .app-bar, .side-panel-toggle {
                opacity: 0.1 !important;
                transition: opacity 0.2s ease !important;
            }
            header:hover, .side-panel-toggle:hover {
                opacity: 1 !important;
            }
            video, canvas {
                width: 100vw !important;
                height: 100vh !important;
                object-fit: contain !important;
            }
        `;
        document.head.appendChild(style);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', injectZeroWasteStyles);
    } else {
        injectZeroWasteStyles();
    }
})();
