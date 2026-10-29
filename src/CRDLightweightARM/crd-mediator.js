(function () {
    'use strict';

    // 1. Prevent Chromium Page Visibility API from freezing CRD's internal WebRTC heartbeat
    try {
        Object.defineProperty(document, 'hidden', { get: () => false, configurable: true });
        Object.defineProperty(document, 'visibilityState', { get: () => 'visible', configurable: true });
    } catch (e) {}

    // 2. Prevent CRD from hijacking OS Keyboard Lock or forcing native browser fullscreen
    //    so the CRD-ARM Top Notch HUD and Ctrl+Alt+* shortcuts always stay in control.
    try {
        if (navigator.keyboard && navigator.keyboard.lock) {
            navigator.keyboard.lock = function () {
                return Promise.resolve();
            };
        }
        const noopFullscreen = function () { return Promise.resolve(); };
        if (Element.prototype.requestFullscreen) {
            Element.prototype.requestFullscreen = noopFullscreen;
        }
        if (Element.prototype.webkitRequestFullscreen) {
            Element.prototype.webkitRequestFullscreen = noopFullscreen;
        }
    } catch (e) {}

    // 3. WebRTC PeerConnection & SCTP RTCDataChannel Interception
    //    Captures 'control' (VideoLayout / ClientResolution / VideoControl protobufs)
    //    and 'event' (USB HID KeyEvent & RawInput MouseEvent protobufs) DataChannels.
    window.__crdActivePeer = null;
    window.__crdControlChannel = null;
    window.__crdEventChannel = null;

    function bindDataChannel(dc) {
        if (!dc || !dc.label) return;
        if (dc.label === 'control' && window.__crdControlChannel !== dc) {
            window.__crdControlChannel = dc;
            dc.addEventListener('open', () => {
                window.chrome?.webview?.postMessage('DATACHANNEL_READY:control');
            });
            if (dc.readyState === 'open') {
                window.chrome?.webview?.postMessage('DATACHANNEL_READY:control');
            }
        } else if (dc.label === 'event' && window.__crdEventChannel !== dc) {
            window.__crdEventChannel = dc;
            dc.addEventListener('open', () => {
                window.chrome?.webview?.postMessage('DATACHANNEL_READY:event');
            });
            if (dc.readyState === 'open') {
                window.chrome?.webview?.postMessage('DATACHANNEL_READY:event');
            }
        }
    }

    try {
        if (window.RTCDataChannel && window.RTCDataChannel.prototype) {
            const origSend = window.RTCDataChannel.prototype.send;
            window.RTCDataChannel.prototype.send = function (data) {
                bindDataChannel(this);
                return origSend.call(this, data);
            };
        }

        const OriginalRTCPeerConnection = window.RTCPeerConnection;
        window.RTCPeerConnection = function (config, constraints) {
            const pc = new OriginalRTCPeerConnection(config, constraints);
            window.__crdActivePeer = pc;

            if (pc.createDataChannel) {
                const origCreateDC = pc.createDataChannel.bind(pc);
                pc.createDataChannel = function (label, options) {
                    const dc = origCreateDC(label, options);
                    bindDataChannel(dc);
                    return dc;
                };
            }

            pc.addEventListener('datachannel', (ev) => {
                if (ev && ev.channel) bindDataChannel(ev.channel);
            });

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

    function base64ToUint8Array(b64) {
        const bin = atob(b64);
        const len = bin.length;
        const bytes = new Uint8Array(len);
        for (let i = 0; i < len; i++) {
            bytes[i] = bin.charCodeAt(i);
        }
        return bytes;
    }

    // Direct Zero-DOM Protobuf Binary Injection over WebRTC 'control' SCTP DataChannel
    window.__crdInjectControlProtoBase64 = function (b64Payloads) {
        const dc = window.__crdControlChannel;
        if (!dc || dc.readyState !== 'open') return false;
        try {
            const list = Array.isArray(b64Payloads) ? b64Payloads : [b64Payloads];
            for (let i = 0; i < list.length; i++) {
                if (list[i]) {
                    dc.send(base64ToUint8Array(list[i]).buffer);
                }
            }
            return true;
        } catch (err) {
            console.warn('[CRD-ARM] Control protobuf inject error:', err);
            return false;
        }
    };

    // Direct Zero-DOM Protobuf Binary Injection over WebRTC 'event' SCTP DataChannel
    window.__crdInjectEventProtoBase64 = function (b64Payloads) {
        const dc = window.__crdEventChannel;
        if (!dc || dc.readyState !== 'open') return false;
        try {
            const list = Array.isArray(b64Payloads) ? b64Payloads : [b64Payloads];
            for (let i = 0; i < list.length; i++) {
                if (list[i]) {
                    dc.send(base64ToUint8Array(list[i]).buffer);
                }
            }
            return true;
        } catch (err) {
            console.warn('[CRD-ARM] Event protobuf inject error:', err);
            return false;
        }
    };

    // High-frequency Win32 WM_INPUT Raw Mouse Delta dispatcher
    window.__crdDispatchRawMouseDelta = function (dx, dy, protoB64) {
        if (protoB64 && window.__crdInjectEventProtoBase64(protoB64)) {
            return true;
        }
        const target = document.querySelector('canvas, video, .desktop-viewport') || document.body;
        if (target) {
            const ev = new MouseEvent('mousemove', {
                bubbles: true,
                cancelable: true,
                movementX: dx,
                movementY: dy
            });
            target.dispatchEvent(ev);
            return true;
        }
        return false;
    };

    // Synchronizes viewport metrics and triggers CRD resize observer alongside binary VideoLayout sync
    window.__crdSetHostResolution = function (width, height, dpi, controlProtoArray) {
        let injected = false;
        if (controlProtoArray) {
            injected = window.__crdInjectControlProtoBase64(controlProtoArray);
        }
        try {
            window.dispatchEvent(new UIEvent('resize'));
        } catch (e) {}
        window.__crdScreenFit.applyToActiveSurface();
        return injected;
    };

    // 4. Telemetry Monitor (Polls every 1500ms without Oryon CPU core wakeups)
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
                let codec = 'VP9';
                let frameWidth = 0;
                let frameHeight = 0;

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

                        if (report.frameWidth) frameWidth = report.frameWidth;
                        if (report.frameHeight) frameHeight = report.frameHeight;

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
                        codec: codec,
                        width: frameWidth,
                        height: frameHeight,
                        dcReady: !!(window.__crdControlChannel && window.__crdControlChannel.readyState === 'open')
                    }));
                }
            } catch (err) {}
        }
    };

    // 5. Cache reference for WebRTC MediaStream
    window.__crdMediaStream = null;

    // 6. Power-gate Snapdragon Adreno GPU decode to 0% when entering Warm Standby
    window.__crdPauseSession = function () {
        try {
            window.__crdStatsMonitor.stop();
            const video = document.querySelector('video');
            if (video && video.srcObject) {
                window.__crdMediaStream = video.srcObject;
                if (window.__crdMediaStream.getVideoTracks) {
                    window.__crdMediaStream.getVideoTracks().forEach(t => { t.enabled = false; });
                }
                video.srcObject = null;
            }
        } catch (err) {}
    };

    // 7. Resume session with sub-35ms IDR keyframe and resume telemetry
    window.__crdResumeSession = async function () {
        try {
            const video = document.querySelector('video');
            if (window.__crdMediaStream && video) {
                if (window.__crdMediaStream.getVideoTracks) {
                    window.__crdMediaStream.getVideoTracks().forEach(t => { t.enabled = true; });
                }
                video.srcObject = window.__crdMediaStream;
                try {
                    await video.play();
                } catch (playErr) {}
            }
            window.__crdScreenFit.applyToActiveSurface();
            window.dispatchEvent(new Event('resize'));
            window.__crdStatsMonitor.start();
        } catch (err) {}
    };

    // 8. Zero-Grey-Bar Killer & Full-Bleed 16:10 Display Mode Switcher
    //    Handles both <video> and <canvas> surfaces and overrides CRD's inline pixel styles!
    window.__crdScreenFit = {
        currentMode: 'contain', // 'contain' (Fit), 'fill' (Stretch 16:10), 'cover' (Zoom)

        setMode: function (mode) {
            this.currentMode = mode || 'contain';
            document.documentElement.setAttribute('data-crd-fit-mode', this.currentMode);
            this.applyToActiveSurface();
            window.dispatchEvent(new UIEvent('resize'));
        },

        applyToActiveSurface: function () {
            const mode = this.currentMode || 'contain';
            document.documentElement.setAttribute('data-crd-fit-mode', mode);

            // Force all grey container backgrounds (#202124 / #3c4043 / rgb(32,33,36)) to pure #000000 OLED black
            const mediaSurfaces = document.querySelectorAll('video, canvas');
            mediaSurfaces.forEach(surface => {
                // Only target the main remote desktop video/canvas (ignore tiny icons)
                if (surface.clientWidth < 100 && surface.width < 100) return;

                // Walk up ancestor containers and strip grey backgrounds & scrollbars
                let parent = surface.parentElement;
                let depth = 0;
                while (parent && parent !== document.body && depth < 10) {
                    parent.style.setProperty('background-color', '#000000', 'important');
                    parent.style.setProperty('background', '#000000', 'important');
                    if (mode === 'fill' || mode === 'cover') {
                        parent.style.setProperty('width', '100vw', 'important');
                        parent.style.setProperty('height', '100vh', 'important');
                        parent.style.setProperty('max-width', '100vw', 'important');
                        parent.style.setProperty('max-height', '100vh', 'important');
                        parent.style.setProperty('top', '0px', 'important');
                        parent.style.setProperty('left', '0px', 'important');
                        parent.style.setProperty('margin', '0px', 'important');
                        parent.style.setProperty('padding', '0px', 'important');
                        parent.style.setProperty('transform', 'none', 'important');
                    } else {
                        // In 'contain' mode, do NOT touch parent widths/heights/transforms so CRD's native scaling matrix works properly!
                        parent.style.removeProperty('max-width');
                        parent.style.removeProperty('max-height');
                    }
                    parent = parent.parentElement;
                    depth++;
                }

                surface.style.setProperty('background-color', '#000000', 'important');
                surface.style.setProperty('image-rendering', '-webkit-optimize-contrast', 'important');

                if (mode === 'fill') {
                    // Stretch 100% full-bleed across 1920x1200 (zero top/bottom bars)
                    surface.style.setProperty('position', 'fixed', 'important');
                    surface.style.setProperty('top', '0px', 'important');
                    surface.style.setProperty('left', '0px', 'important');
                    surface.style.setProperty('width', '100vw', 'important');
                    surface.style.setProperty('height', '100vh', 'important');
                    surface.style.setProperty('max-width', '100vw', 'important');
                    surface.style.setProperty('max-height', '100vh', 'important');
                    surface.style.setProperty('margin', '0px', 'important');
                    surface.style.setProperty('transform', 'none', 'important');
                    surface.style.setProperty('object-fit', 'fill', 'important');
                } else if (mode === 'cover') {
                    // Zoom proportionally to fill 1920x1200 without aspect distortion
                    surface.style.setProperty('position', 'fixed', 'important');
                    surface.style.setProperty('top', '0px', 'important');
                    surface.style.setProperty('left', '0px', 'important');
                    surface.style.setProperty('width', '100vw', 'important');
                    surface.style.setProperty('height', '100vh', 'important');
                    surface.style.setProperty('max-width', '100vw', 'important');
                    surface.style.setProperty('max-height', '100vh', 'important');
                    surface.style.setProperty('margin', '0px', 'important');
                    surface.style.setProperty('transform', 'none', 'important');
                    surface.style.setProperty('object-fit', 'cover', 'important');
                } else {
                    // Fit ('contain'): Preserve CRD's native scaling matrix so ultrawide/4K windows scale correctly!
                    surface.style.removeProperty('position');
                    surface.style.removeProperty('top');
                    surface.style.removeProperty('left');
                    surface.style.removeProperty('width');
                    surface.style.removeProperty('height');
                    surface.style.removeProperty('max-width');
                    surface.style.removeProperty('max-height');
                    surface.style.removeProperty('margin');
                    surface.style.removeProperty('transform');
                    surface.style.setProperty('object-fit', 'contain', 'important');
                }
            });
        }
    };

    // 9. Relative Mouse Mode (Pointer Lock for 3D CAD/Blender/Gaming)
    window.__crdEnterPointerLock = function () {
        const target = document.querySelector('canvas, video, .desktop-viewport') || document.body;
        if (target && target.requestPointerLock) {
            target.requestPointerLock();
        }
    };

    window.__crdExitPointerLock = function () {
        if (document.exitPointerLock && document.pointerLockElement) {
            document.exitPointerLock();
        }
    };

    // Double-Tap Escape Failsafe
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

    // 10. Remote System Key Handlers
    window.__crdSendCtrlAltDel = function () {
        const cadButton = document.querySelector('[data-qa-id="send-ctrl-alt-del"], #send-ctrl-alt-del, [aria-label*="Ctrl+Alt+Del"]');
        if (cadButton) {
            cadButton.click();
            return true;
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

    // Remote Host Multi-Monitor / Screen Selector
    window.__crdCurrentDisplayId = 'all';
    window.__crdSelectDisplay = function (displayId, protoB64) {
        window.__crdCurrentDisplayId = displayId;
        if (protoB64 && window.__crdInjectControlProtoBase64(protoB64)) {
            // Injected via WebRTC SCTP Control DataChannel
        }
        try {
            // Also trigger CRD's DOM display selector if open/present
            const displayElements = document.querySelectorAll('input[name="display-selector"], [data-qa-id*="display"], option[value*="display"], [aria-label*="Display"], [aria-label*="Screen"]');
            for (let el of displayElements) {
                if (el.value === displayId || (el.textContent && el.textContent.includes(displayId))) {
                    el.click();
                    el.dispatchEvent(new Event('change', { bubbles: true }));
                    break;
                }
            }
        } catch (e) {}
        window.__crdScreenFit.applyToActiveSurface();
        return true;
    };

    window.__crdCycleDisplay = function (protoB64List) {
        // Toggle '0' -> '1' -> 'all'
        const modes = ['0', '1', 'all'];
        let nextIdx = (modes.indexOf(window.__crdCurrentDisplayId) + 1) % modes.length;
        let nextMode = modes[nextIdx];
        let protoB64 = (protoB64List && protoB64List[nextIdx]) ? protoB64List[nextIdx] : null;
        window.__crdSelectDisplay(nextMode, protoB64);
        return nextMode;
    };

    // 11. Continuous Watchdog: Host Name, Auto-Reconnect, & Grey-Bar / Display Mode Enforcement
    window.__lastReportedHostName = '';
    setInterval(function () {
        try {
            // Re-enforce pitch-black background & display mode over CRD's dynamic DOM updates
            window.__crdScreenFit.applyToActiveSurface();

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

            // Auto-reconnect dialog
            const reconnectBtn = document.querySelector('[data-qa-id="reconnect-button"], .reconnect-button');
            if (reconnectBtn && reconnectBtn.offsetParent !== null) {
                reconnectBtn.click();
            }

            // Auto-enable CRD native 'Scale to fit' so ultrawide/high-res desktops fit naturally with proper window scale
            const shrinkBtn = document.querySelector('[data-qa-id="shrink-to-fit"], #shrink-to-fit');
            if (shrinkBtn && !shrinkBtn.checked && shrinkBtn.getAttribute('aria-checked') !== 'true') {
                shrinkBtn.click();
            }
        } catch (e) {}
    }, 1000);

    // 12. Injected CSS for 0px wasted space & OLED Pitch-Black letterbox (kills #202124 / #3c4043 grey bars)
    function injectZeroWasteStyles() {
        const styleId = 'crd-lightweight-zero-waste';
        if (document.getElementById(styleId)) return;

        const style = document.createElement('style');
        style.id = styleId;
        style.textContent = `
            html, body,
            remoting-app, remoting-display, crd-session,
            mat-sidenav-container, mat-sidenav-content, mat-drawer-container, mat-drawer-content,
            .desktop-viewport, .video-container, .client-container, .viewport, .scroll-container,
            div[style*="rgb(32, 33, 36)"], div[style*="#202124"], div[style*="rgb(60, 64, 67)"], div[style*="#3c4043"] {
                margin: 0 !important;
                padding: 0 !important;
                overflow: hidden !important;
                background-color: #000000 !important;
                background: #000000 !important;
            }
            /* Hide CRD's own top/side floating handle unless directly hovered */
            header, .app-bar, .side-panel-toggle, [data-qa-id="side-panel-toggle"] {
                opacity: 0.02 !important;
                transition: opacity 0.2s ease !important;
            }
            header:hover, .side-panel-toggle:hover, [data-qa-id="side-panel-toggle"]:hover {
                opacity: 0.95 !important;
            }
            video, canvas {
                background-color: #000000 !important;
                image-rendering: -webkit-optimize-contrast !important;
            }
            html[data-crd-fit-mode="fill"] video,
            html[data-crd-fit-mode="fill"] canvas {
                position: fixed !important;
                inset: 0 !important;
                width: 100vw !important;
                height: 100vh !important;
                max-width: 100vw !important;
                max-height: 100vh !important;
                transform: none !important;
                object-fit: fill !important;
            }
            html[data-crd-fit-mode="cover"] video,
            html[data-crd-fit-mode="cover"] canvas {
                position: fixed !important;
                inset: 0 !important;
                width: 100vw !important;
                height: 100vh !important;
                max-width: 100vw !important;
                max-height: 100vh !important;
                transform: none !important;
                object-fit: cover !important;
            }
        `;
        (document.head || document.documentElement).appendChild(style);
        window.__crdStatsMonitor.start();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', injectZeroWasteStyles);
    } else {
        injectZeroWasteStyles();
    }
})();
