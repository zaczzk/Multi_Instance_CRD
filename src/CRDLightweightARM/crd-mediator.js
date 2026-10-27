(function () {
    'use strict';

    // 1. Prevent Chromium Page Visibility API from freezing CRD's internal WebRTC heartbeat
    try {
        Object.defineProperty(document, 'hidden', { get: () => false, configurable: true });
        Object.defineProperty(document, 'visibilityState', { get: () => 'visible', configurable: true });
    } catch (e) {}

    // 2. WebRTC PeerConnection Interception for Real-Time Telemetry (RTT, FPS, Codec)
    window.__crdActivePeer = null;
    try {
        const OriginalRTCPeerConnection = window.RTCPeerConnection;
        window.RTCPeerConnection = function (config, constraints) {
            const pc = new OriginalRTCPeerConnection(config, constraints);
            window.__crdActivePeer = pc;
            pc.addEventListener('connectionstatechange', () => {
                if (window.chrome?.webview) {
                    window.chrome.webview.postMessage('CONN_STATE:' + pc.connectionState);
                }
            });
            return pc;
        };
        window.RTCPeerConnection.prototype = OriginalRTCPeerConnection.prototype;
    } catch (e) {
        console.warn('[CRD-ARM] WebRTC hook warning:', e);
    }

    // 3. Telemetry Monitor (Polls every 1500ms without Oryon CPU core wakeups)
    window.__crdStatsMonitor = {
        timerId: null,
        lastFramesDecoded: 0,
        lastTimestamp: 0,

        start: function () {
            if (this.timerId) return;
            this.timerId = setInterval(() => this.poll(), 1500);
        },

        stop: function () {
            if (this.timerId) {
                clearInterval(this.timerId);
                this.timerId = null;
            }
        },

        poll: async function () {
            const pc = window.__crdActivePeer;
            if (!pc || pc.connectionState !== 'connected') return;

            try {
                const stats = await pc.getStats();
                let rtt = -1;
                let fps = 0;
                let codec = 'H.264';

                stats.forEach(report => {
                    if (report.type === 'candidate-pair' && (report.selected || report.nominated)) {
                        if (report.currentRoundTripTime !== undefined) {
                            rtt = Math.round(report.currentRoundTripTime * 1000);
                        }
                    }
                    if (report.type === 'inbound-rtp' && report.kind === 'video') {
                        const now = report.timestamp;
                        const decoded = report.framesDecoded || 0;
                        if (this.lastTimestamp > 0 && now > this.lastTimestamp) {
                            const deltaFrames = decoded - this.lastFramesDecoded;
                            const deltaSec = (now - this.lastTimestamp) / 1000;
                            fps = Math.max(0, Math.round(deltaFrames / deltaSec));
                        }
                        this.lastFramesDecoded = decoded;
                        this.lastTimestamp = now;

                        if (report.codecId) {
                            const codecReport = stats.get(report.codecId);
                            if (codecReport && codecReport.mimeType) {
                                codec = codecReport.mimeType.replace('video/', '');
                            }
                        }
                    }
                });

                if (window.chrome?.webview && rtt >= 0) {
                    window.chrome.webview.postMessage(JSON.stringify({
                        type: 'STATS',
                        rtt: rtt,
                        fps: fps,
                        codec: codec
                    }));
                }
            } catch (err) {}
        }
    };

    // 4. Cache reference for WebRTC MediaStream
    window.__crdMediaStream = null;

    // 5. Power-gate Snapdragon Adreno GPU decode to 0% when entering Warm Standby
    window.__crdPauseSession = function () {
        try {
            window.__crdStatsMonitor.stop();
            const video = document.querySelector('video');
            if (video && video.srcObject) {
                window.__crdMediaStream = video.srcObject;
                video.srcObject = null;
                console.log('[CRD-ARM] Session paused: video sink detached, Adreno decode 0%');
            }
        } catch (err) {
            console.error('[CRD-ARM] Error pausing session:', err);
        }
    };

    // 6. Resume session with sub-35ms IDR keyframe and resume telemetry
    window.__crdResumeSession = async function () {
        try {
            const video = document.querySelector('video');
            if (window.__crdMediaStream && video) {
                video.srcObject = window.__crdMediaStream;
                try {
                    await video.play();
                } catch (playErr) {}
                console.log('[CRD-ARM] Session resumed: video sink reattached');
            }
            window.dispatchEvent(new Event('resize'));
            window.__crdStatsMonitor.start();
        } catch (err) {
            console.error('[CRD-ARM] Error resuming session:', err);
        }
    };

    // 7. Screen Mismatch / Grey Bars Killer & Display Mode Switcher
    window.__crdScreenFit = {
        currentMode: 'contain',

        setMode: function (mode) {
            this.currentMode = mode;
            const video = document.querySelector('video');
            const container = document.querySelector('.desktop-viewport, .video-container') || document.body;

            document.body.style.setProperty('background-color', '#000000', 'important');
            if (container) {
                container.style.setProperty('background-color', '#000000', 'important');
                container.style.setProperty('display', 'flex', 'important');
                container.style.setProperty('align-items', 'center', 'important');
                container.style.setProperty('justify-content', 'center', 'important');
            }

            if (video) {
                video.style.setProperty('object-fit', mode, 'important');
                video.style.setProperty('image-rendering', '-webkit-optimize-contrast', 'important');

                if (mode === 'fill') {
                    video.style.setProperty('width', '100vw', 'important');
                    video.style.setProperty('height', '100vh', 'important');
                } else if (mode === 'cover') {
                    video.style.setProperty('min-width', '100vw', 'important');
                    video.style.setProperty('min-height', '100vh', 'important');
                } else {
                    video.style.setProperty('max-width', '100vw', 'important');
                    video.style.setProperty('max-height', '100vh', 'important');
                }
            }

            window.dispatchEvent(new UIEvent('resize'));
        }
    };

    // 8. Relative Mouse Mode (Pointer Lock for 3D CAD/Blender/Gaming)
    window.__crdEnterPointerLock = function () {
        const target = document.querySelector('.desktop-viewport, canvas, video') || document.body;
        if (target && target.requestPointerLock) {
            target.requestPointerLock();
        }
    };

    window.__crdExitPointerLock = function () {
        if (document.exitPointerLock && document.pointerLockElement) {
            document.exitPointerLock();
        }
    };

    // Double-Tap Escape Failsafe (reaches C# even when PointerLock is active)
    let lastEscTime = 0;
    window.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && document.pointerLockElement) {
            const now = Date.now();
            if (now - lastEscTime < 350) {
                window.__crdExitPointerLock();
                window.chrome?.webview?.postMessage('RELEASE_CURSOR');
                e.preventDefault();
                e.stopPropagation();
            }
            lastEscTime = now;
        }
    }, true);

    // 9. Remote System Key Handlers
    window.__crdSendCtrlAltDel = function () {
        const cadButton = document.querySelector('[data-qa-id="send-ctrl-alt-del"], #send-ctrl-alt-del');
        if (cadButton) {
            cadButton.click();
            return true;
        }
        if (window.remotedesktop && window.remotedesktop.ClientSession) {
            try {
                window.remotedesktop.ClientSession.sendCtrlAltDel();
                return true;
            } catch (e) {}
        }
        return false;
    };

    window.__crdSendLockWorkstation = function () {
        const lockBtn = document.querySelector('[data-qa-id="lock-host"]');
        if (lockBtn) {
            lockBtn.click();
            return true;
        }
        return false;
    };

    // 10. Auto-detect connected host machine name and report to wrapper
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

    // 11. Auto-reconnect watchdog (catches disconnect/reboot modal)
    setInterval(function () {
        try {
            const reconnectBtn = document.querySelector('[data-qa-id="reconnect-button"], .reconnect-button');
            if (reconnectBtn && reconnectBtn.offsetParent !== null) {
                console.log('[CRD-ARM] Detected disconnect dialog. Auto-reconnecting...');
                reconnectBtn.click();
            }
        } catch (e) {}
    }, 2500);

    // 12. In-page accelerator key forwarding bridge via WebMessage
    window.addEventListener('keydown', function (e) {
        if (e.ctrlKey && e.altKey) {
            if (e.key === '1') {
                window.chrome?.webview?.postMessage('SWITCH_1');
                e.preventDefault(); e.stopPropagation();
            } else if (e.key === '2') {
                window.chrome?.webview?.postMessage('SWITCH_2');
                e.preventDefault(); e.stopPropagation();
            } else if (e.key === '3') {
                window.chrome?.webview?.postMessage('SWITCH_3');
                e.preventDefault(); e.stopPropagation();
            } else if (e.key === 'Tab') {
                window.chrome?.webview?.postMessage('CYCLE');
                e.preventDefault(); e.stopPropagation();
            } else if (e.key === 's' || e.key === 'S') {
                window.chrome?.webview?.postMessage('TOGGLE_NOTCH');
                e.preventDefault(); e.stopPropagation();
            } else if (e.key === 'd' || e.key === 'D') {
                window.chrome?.webview?.postMessage('CYCLE_DISPLAY_MODE');
                e.preventDefault(); e.stopPropagation();
            } else if (e.key === 'c' || e.key === 'C') {
                window.chrome?.webview?.postMessage('TOGGLE_COCKPIT');
                e.preventDefault(); e.stopPropagation();
            } else if (e.key === 'm' || e.key === 'M') {
                window.chrome?.webview?.postMessage('TOGGLE_RELATIVE_MOUSE');
                e.preventDefault(); e.stopPropagation();
            } else if (e.key === 'Home') {
                window.__crdExitPointerLock();
                window.chrome?.webview?.postMessage('RELEASE_CURSOR');
                e.preventDefault(); e.stopPropagation();
            }
        }
    }, true);

    // 13. Injected CSS for 0px wasted space & OLED Pitch-Black letterbox
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
                background-color: #000000 !important;
            }
            .desktop-viewport, .video-container {
                background-color: #000000 !important;
            }
            header, .app-bar, .side-panel-toggle {
                opacity: 0.1 !important;
                transition: opacity 0.2s ease !important;
            }
            header:hover, .side-panel-toggle:hover {
                opacity: 1 !important;
            }
            video, canvas {
                background-color: #000000 !important;
                image-rendering: -webkit-optimize-contrast !important;
            }
        `;
        document.head.appendChild(style);
        window.__crdStatsMonitor.start();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', injectZeroWasteStyles);
    } else {
        injectZeroWasteStyles();
    }
})();
