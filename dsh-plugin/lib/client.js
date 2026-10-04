/**
 * dsh-remote-mic 插件 —— 第 1 步：挂载 + 连接 local-mic + 显示状态。
 *
 * ## 为什么这个文件是手写的、没有构建步骤
 *
 * dsh 的客户端插件产物是 `window.__ModuleLoader__` 包装的一层：factory 拿到的 `require`
 * 是运行时的模块解析器，React / react-dom 都是**外部引用、不内联**。参考插件
 * （dsh-better-sidebar/lib/client.js）用的是 tsdown/rolldown 打出来的产物，但那份产物
 * 额外做的事只有「把 JSX 编译成 createElement」和「把 CJS 包成 ESM 命名空间」。
 *
 * 逻辑很薄，用 `React.createElement` 就够 ⇒ **不需要 TypeScript、不需要打包器**，
 * 也就不给这个仓库引入一条构建链。等真的需要 JSX / 多文件拆分时再上工具链，那时再构建。
 * 样式同理：运行时注入一张 <style>，不引 CSS 文件、不引 CSS-in-JS 依赖。
 *
 * 产物契约（照 `client-ui-voice-input` 的 compiled 产物）：
 *   exports.inject = 需要注入的 ctx 键
 *   exports.apply  = 入口 async (ctx) => disposer
 *
 * 设计依据见 docs/DSH-SEAMS.md §7。
 */
window.__ModuleLoader__.load({
  id: 'dsh-remote-mic-plugin',
  factory: (require) => {
    var module = { exports: {} };
    var exports = module.exports;

    var React = require('react');
    // 参考产物用 __toESM 兜了一层（require 可能是 CJS 也可能是 ESM 命名空间）。
    if (React && React.default && !React.createElement) React = React.default;
    var h = React.createElement;

    var ReactDOM = require('react-dom');
    if (ReactDOM && ReactDOM.default && !ReactDOM.createPortal) ReactDOM = ReactDOM.default;

    // 框架 UI 原语（平台预置模块，不需要安装）：官方「会话统计」面板用的就是这两个 hook，
    // 我们用同一套，从而不再自己写「点外面关」和定位；Switch 用于详情页里的拦截开关。
    var primitives = require('@deepseek-ai/dsh-client-ui-primitives');
    if (primitives && primitives.default && !primitives.useAnchoredPosition) primitives = primitives.default;
    var Switch = primitives.Switch;

    // ------------------------------------------------------------------
    // 接缝常量 —— 线缆协议 proto 1 已冻结，唯一真源是 docs/PROTOCOL.md。
    // 这里只抄接缝字段，不出现任何设备词汇（AGENTS.md 定案 A4）。
    // ------------------------------------------------------------------
    var PROTO_VERSION = 1;
    var DEFAULT_PORT = 8787;
    var PORT_STORAGE_KEY = 'dsh-remote-mic.port';
    var INTERCEPT_STORAGE_KEY = 'dsh-remote-mic.interceptRefresh';
    var BUNDLE_KEY = 'dsh-remote-mic-plugin';

    // 接缝编码是定案 A9：PCM16 s16le / 16 kHz / 单声道 ⇒ 32 000 B/s = 32 B/ms。
    // 时长一律由**字节数**换算，不用帧数（PROTOCOL.md 不变量 5：帧边界不承载时间语义）。
    var SAMPLE_RATE = 16000;
    var BYTES_PER_MS = SAMPLE_RATE * 2 / 1000;

    function readPort() {
      try {
        var raw = window.localStorage.getItem(PORT_STORAGE_KEY);
        var n = raw ? parseInt(raw, 10) : NaN;
        return n > 0 && n < 65536 ? n : DEFAULT_PORT;
      } catch (e) {
        return DEFAULT_PORT;
      }
    }

    // 「是否拦截刷新键」的用户偏好：跟着浏览器走（和端口一样），这样换台电脑、没带遥控器时
    // 也能手动拨开关调试。默认 false —— 没有连接事件之前不夺走用户自己的 F5。
    function readInterceptPref() {
      try { return window.localStorage.getItem(INTERCEPT_STORAGE_KEY) === '1'; } catch (e) { return false; }
    }

    function writeInterceptPref(on) {
      try { window.localStorage.setItem(INTERCEPT_STORAGE_KEY, on ? '1' : '0'); } catch (e) { /* 无痕模式等 */ }
    }

    function writePort(port) {
      try { window.localStorage.setItem(PORT_STORAGE_KEY, String(port)); } catch (e) { /* 无痕模式等 */ }
    }

    // ------------------------------------------------------------------
    // 样式：运行时注入一次。用 Canvas / CanvasText 这类系统色，
    // 天然跟随 dsh 的浅色 / 深色主题，不用去猜它的 CSS 变量名。
    // ------------------------------------------------------------------
    var STYLE_ID = 'dsh-remote-mic-style';
    var CSS = [
      '.dsh-rm{position:relative;display:inline-flex;align-items:center}',
      '.dsh-rm-btn{display:inline-flex;align-items:center;justify-content:center;',
      'width:32px;height:32px;padding:0;border:0;border-radius:8px;background:transparent;',
      'color:#888780;cursor:pointer;transition:color .15s,background .15s}',
      '.dsh-rm-btn:hover{background:color-mix(in srgb,CanvasText 8%,Canvas)}',
      '.dsh-rm-btn:focus-visible{outline:2px solid #4C8DF6;outline-offset:1px}',
      '.dsh-rm-btn[data-phase="connecting"]{color:#EF9F27}',
      '.dsh-rm-btn[data-phase="ready"]{color:#1D9E75}',
      '.dsh-rm-btn[data-phase="error"]{color:#E24B4A}',
      // 录音中：图标外圈脉冲。这是「硬件 PTT 正在进行」的唯一提示
      '.dsh-rm-btn[data-rec="1"]::after{content:"";position:absolute;inset:-3px;',
      'border-radius:10px;border:2px solid #1D9E75;animation:dsh-rm-pulse 1.1s ease-out infinite}',
      '@keyframes dsh-rm-pulse{0%{opacity:.85;transform:scale(.9)}100%{opacity:0;transform:scale(1.18)}}',
      // 详情浮层：portal 到 body + 固定定位（top/left 由 useAnchoredPosition 给），
      // 所以这里不写 bottom/right，也不再受输入框容器的 overflow / 层叠上下文裁剪。
      '.dsh-rm-pop{position:fixed;z-index:60;min-width:236px;',
      'padding:10px 12px;border-radius:10px;border:1px solid color-mix(in srgb,CanvasText 14%,Canvas);',
      'background:Canvas;color:CanvasText;font-size:12px;line-height:1.55;',
      'box-shadow:0 8px 28px rgba(0,0,0,.18);text-align:left}',
      '.dsh-rm-pop h4{margin:0 0 6px;font-size:12px;font-weight:600}',
      '.dsh-rm-pop dl{display:grid;grid-template-columns:auto 1fr;gap:2px 10px;margin:0}',
      '.dsh-rm-pop dt{opacity:.6;white-space:nowrap}',
      '.dsh-rm-pop dd{margin:0;word-break:break-all}',
      '.dsh-rm-pop .dsh-rm-actions{display:flex;align-items:center;gap:10px;margin-top:8px;',
      'padding-top:8px;border-top:1px solid color-mix(in srgb,CanvasText 12%,Canvas)}',
      '.dsh-rm-link{border:0;background:none;color:#4C8DF6;cursor:pointer;padding:0;font:inherit}',
      '.dsh-rm-link:hover{text-decoration:underline}',
      // 设置页
      '.dsh-rm-set{display:flex;flex-direction:column;gap:8px}',
      '.dsh-rm-set label{font-size:12px;opacity:.75}',
      '.dsh-rm-set input{width:120px;padding:4px 6px;border-radius:6px;',
      'border:1px solid color-mix(in srgb,CanvasText 20%,Canvas);background:Canvas;color:CanvasText}',
    ].join('');

    function ensureStyle() {
      if (document.getElementById(STYLE_ID)) return;
      var el = document.createElement('style');
      el.id = STYLE_ID;
      el.textContent = CSS;
      document.head.appendChild(el);
    }

    // ------------------------------------------------------------------
    // WAV 组装：接缝给的是裸 PCM16 样本流，转写 seam 要的是音频文件字节。
    // 只补 44 字节标准 RIFF/WAVE 头（PCM、单声道、16 bit、16 kHz），不做任何
    // 重采样或增益 —— v1 不做增益是定案 A9，要加属破坏性变更得抬 proto。
    // ------------------------------------------------------------------
    function pcmToWav(chunks, totalBytes) {
      var wav = new ArrayBuffer(44 + totalBytes);
      var view = new DataView(wav);
      var ascii = function (offset, text) {
        for (var i = 0; i < text.length; i++) view.setUint8(offset + i, text.charCodeAt(i));
      };
      ascii(0, 'RIFF');
      view.setUint32(4, 36 + totalBytes, true);
      ascii(8, 'WAVE');
      ascii(12, 'fmt ');
      view.setUint32(16, 16, true);                     // fmt chunk 长度
      view.setUint16(20, 1, true);                      // 编码 = PCM
      view.setUint16(22, 1, true);                      // 声道 = 单声道
      view.setUint32(24, SAMPLE_RATE, true);
      view.setUint32(28, SAMPLE_RATE * 2, true);        // 字节率 = 采样率 × 声道 × 2
      view.setUint16(32, 2, true);                      // block align
      view.setUint16(34, 16, true);                     // 位深
      ascii(36, 'data');
      view.setUint32(40, totalBytes, true);
      var out = new Uint8Array(wav, 44);
      var offset = 0;
      for (var i = 0; i < chunks.length; i++) {
        out.set(chunks[i], offset);
        offset += chunks[i].length;
      }
      return wav;
    }

    // ------------------------------------------------------------------
    // Seam：与 local-mic 的连接。模块级单例 —— 一个页面只该有一条接缝连接，
    // 否则会重复订阅音频（DSH-SEAMS §3.1 提过「僵尸连接收尸」那类问题）。
    // ------------------------------------------------------------------
    function Seam() {
      this.ws = null;
      this.port = readPort();
      this.state = {
        phase: 'idle',        // idle | connecting | ready | error
        detail: '未启动',
        device: null,         // { name, model, battery, paired }
        deviceState: null,    // 协议 §8.1 的机读枚举：disconnected | connecting | connected | error
        proto: null,
        audioBytes: 0,        // 本段已收音频字节数（只统计 0x02 的载荷）
        durationMs: 0,        // 由字节数换算的时长（不信任帧边界，见 PROTOCOL.md 不变量 5）
        capturing: false,
        lastClip: null,       // { wav: ArrayBuffer, bytes, durationMs } —— 上一段收齐的成品
        lastError: null,
        interceptRefresh: readInterceptPref(),   // 是否拦截刷新键（用户可手动拨，随连接事件自动开合）
      };
      this.chunks = [];       // 本段累积的 PCM16 载荷
      this.capturedBytes = 0;
      this.warnedKinds = {};  // 未知 kind 只告警一次，不刷屏
      this.listeners = new Set();
      this.retryTimer = null;
      this.retryDelay = 500;
      this.disposed = false;
    }

    Seam.prototype.subscribe = function (fn) {
      this.listeners.add(fn);
      return () => this.listeners.delete(fn);
    };

    Seam.prototype.emit = function (patch) {
      if (patch) Object.assign(this.state, patch);
      // 必须给订阅者一份**新对象**：this.state 是被原地改的，React 的 useState 用
      // Object.is 比较，收到同一个引用会直接 bail out ⇒ 图标颜色/浮层字段永远不刷新。
      var snapshot = Object.assign({}, this.state);
      this.listeners.forEach(function (fn) {
        try { fn(snapshot); } catch (e) { /* 单个订阅者出错不能连累其他人 */ }
      });
    };

    Seam.prototype.url = function () {
      return 'ws://127.0.0.1:' + this.port;
    };

    Seam.prototype.connect = function () {
      var self = this;
      if (this.disposed) return;
      if (this.ws && (this.ws.readyState === 0 || this.ws.readyState === 1)) return;

      this.emit({ phase: 'connecting', detail: '连接 local-mic…', lastError: null });

      var ws;
      try {
        ws = new WebSocket(this.url());
      } catch (e) {
        this.emit({ phase: 'error', detail: '无法连接', lastError: String(e && e.message || e) });
        this.scheduleRetry();
        return;
      }
      ws.binaryType = 'arraybuffer';
      this.ws = ws;

      ws.onopen = function () {
        self.retryDelay = 500;
        // 接缝握手：proto 不匹配会被服务端 error + 断开，这里不做任何本地猜测。
        ws.send(JSON.stringify({ op: 'hello', proto: PROTO_VERSION, audio: true }));
      };

      ws.onmessage = function (ev) {
        if (typeof ev.data === 'string') self.onText(ev.data);
        else self.onBinary(ev.data);
      };

      ws.onerror = function () {
        // onerror 本身不给可读原因，真正的判定在 onclose。
      };

      ws.onclose = function () {
        self.ws = null;
        if (self.disposed) return;
        // 接缝断了 = 遥控器这条路不通了 ⇒ 把刷新键交还用户（【断开】= 关闭拦截）
        self.emit({ phase: 'idle', detail: '未连接', capturing: false, device: null, proto: null, deviceState: null, interceptRefresh: false });
        self.scheduleRetry();
      };
    };

    /** 接缝文本消息。只有已冻结的 op；未知 op 静默忽略（PROTOCOL.md §4）。 */
    Seam.prototype.onText = function (text) {
      var msg;
      try { msg = JSON.parse(text); } catch (e) { return; }
      if (!msg || typeof msg.op !== 'string') return;

      switch (msg.op) {
        case 'state':
          // 协议 §8.1.1：state 是「状态」不是「事件」——只在取值变化时推送、connected 只发一次。
          // 所以这里按**取值边沿**同步拦截开关，而不是等某个"连接成功事件"：
          //   connected ⇒【遥控器连接成功】= 打开拦截；其它取值（含未知）⇒【断开/未就绪】= 关闭拦截。
          this.emit({
            deviceState: msg.state || null,
            detail: msg.detail || msg.state || '',
            interceptRefresh: msg.state === 'connected',
          });
          break;
        case 'device':
          this.emit({
            device: {
              name: msg.name || '', model: msg.model || '',
              battery: typeof msg.battery === 'number' ? msg.battery : null,
              paired: !!msg.paired,
            },
          });
          break;
        case 'ready':
          this.emit({ phase: 'ready', proto: msg.proto, detail: '接缝就绪' });
          break;
        case 'capture':
          // start..end 必然成对（PROTOCOL.md §8.2.5）。区间外的音频一律不计（不变量 7）。
          if (msg.phase === 'end') this.finishClip();
          else this.beginClip();
          break;
        case 'error':
          this.emit({ phase: 'error', lastError: msg.message || msg.code || '对端报错' });
          break;
        default:
          break; // 未知 op：静默忽略，连接保持（与夹具一致）
      }
    };

    /**
     * 二进制帧（PROTOCOL.md §10）：1 字节 kind + payload。
     * proto 1 只定义 0x02 = PCM16 样本流；未知 kind 忽略并告警一次，不改连接状态。
     * 设备无关：这里不出现任何 ATVV / 120 字节 / ADPCM 之类的硬件词汇（定案 A4）。
     */
    Seam.prototype.onBinary = function (buffer) {
      if (!this.state.capturing) return;                 // 不变量 7：区间外忽略
      var bytes = new Uint8Array(buffer);
      if (bytes.length < 1) return;
      var kind = bytes[0];
      if (kind !== 0x02) {
        if (!this.warnedKinds[kind]) {
          this.warnedKinds[kind] = true;
          window.console.warn('[dsh-remote-mic] 未知二进制 kind 0x' + kind.toString(16) + '，已忽略（PROTOCOL.md §10）');
        }
        return;
      }
      var payload = bytes.subarray(1);
      if (payload.length === 0 || payload.length % 2 !== 0) return;  // PCM16 载荷必须为偶数
      this.chunks.push(payload);
      this.capturedBytes += payload.length;
      this.emit({
        audioBytes: this.capturedBytes,
        durationMs: Math.round(this.capturedBytes / BYTES_PER_MS),
      });
    };

    /** 开一段：清空累积。抢跑/迟到的事件这里不做语义判断，一切以事件为准。 */
    Seam.prototype.beginClip = function () {
      this.chunks = [];
      this.capturedBytes = 0;
      this.emit({ capturing: true, audioBytes: 0, durationMs: 0 });
    };

    /** 收一段：组 WAV 存进 state.lastClip，供试听与（下一步）送转写。 */
    Seam.prototype.finishClip = function () {
      var bytes = this.capturedBytes;
      var clip = bytes > 0
        ? {
            wav: pcmToWav(this.chunks, bytes),
            bytes: bytes,
            durationMs: Math.round(bytes / BYTES_PER_MS),
          }
        : null;
      this.chunks = [];
      this.capturedBytes = 0;
      this.emit({ capturing: false, audioBytes: 0, durationMs: 0, lastClip: clip });
    };

    Seam.prototype.scheduleRetry = function () {
      var self = this;
      if (this.disposed || this.retryTimer) return;
      this.retryTimer = window.setTimeout(function () {
        self.retryTimer = null;
        self.connect();
      }, this.retryDelay);
      this.retryDelay = Math.min(this.retryDelay * 2, 8000); // 指数退避，封顶 8 s
    };

    /** 换端口 = 换一条接缝，必须重建连接。 */
    Seam.prototype.setPort = function (port) {
      var n = parseInt(port, 10);
      if (!(n > 0 && n < 65536) || n === this.port) return false;
      writePort(n);
      this.port = n;
      if (this.ws) { try { this.ws.onclose = null; this.ws.close(); } catch (e) { /* noop */ } this.ws = null; }
      this.emit({ phase: 'idle', detail: '未连接', capturing: false, device: null, proto: null, deviceState: null, interceptRefresh: false });
      this.connect();
      return true;
    };

    /**
     * 手动开合「拦截刷新键」——详情页的开关走这里。
     * 会被下一次 state 取值变化重新同步（connected 打开、其它关闭），这是刻意的：
     * 手动拨的意义在于"没有遥控器时也能调试"，而不是覆盖连接事实。
     */
    Seam.prototype.setInterceptRefresh = function (on) {
      var next = !!on;
      writeInterceptPref(next);
      this.emit({ interceptRefresh: next });
      return next;
    };

    Seam.prototype.dispose = function () {
      this.disposed = true;
      if (this.retryTimer) { window.clearTimeout(this.retryTimer); this.retryTimer = null; }
      if (this.ws) { try { this.ws.onclose = null; this.ws.close(); } catch (e) { /* noop */ } this.ws = null; }
      this.listeners.clear();
    };

    var seam = new Seam();

    // ------------------------------------------------------------------
    // 刷新键拦截 —— 遥控器的语音键在 Windows 上**就是裸 F5**，按住时系统会连续发几十次
    // ⇒ 页面不停刷新、WebSocket 断开、录音中断（HARDWARE §4.1.1 真机实测）。
    //
    // 定案（DSH-SEAMS §7.5）：**仅接缝 ready 时接管**，平时把刷新键交还浏览器。
    // 只拦裸 F5 / BrowserRefresh —— Ctrl+R、Ctrl+F5、Shift+F5 是用户自己的操作，一律放行
    // （与已真机验证的诊断页 diagnostics/js/app.js 同一口径）。代价：连接期间手动 F5 也无效，
    // 要刷新请用 Ctrl+R / Ctrl+F5 —— 这一点在浮层里明说（§7.5 要求"在 UI 上明说已接管"）。
    // ------------------------------------------------------------------
    function isGuardedRefreshKey(e) {
      var isRefresh = e.key === 'F5' || e.code === 'F5' || e.code === 'BrowserRefresh';
      if (!isRefresh) return false;
      return !(e.ctrlKey || e.metaKey || e.altKey || e.shiftKey);
    }

    /** 挂一次全局 keydown（捕获阶段，早于页面其它处理）；返回 disposer。 */
    function installRefreshKeyGuard() {
      var onKeyDown = function (e) {
        if (!seam.state.interceptRefresh) return;   // 未接管：交还浏览器
        if (!isGuardedRefreshKey(e)) return;
        e.preventDefault();                          // 只吞默认行为，不 stopPropagation
      };
      window.addEventListener('keydown', onKeyDown, true);
      return function () { window.removeEventListener('keydown', onKeyDown, true); };
    }

    // ------------------------------------------------------------------
    // UI —— 刻意克制：输入框里**只放一个图标**，状态用颜色表达。
    // 详情点开浮层看，设置进设置页。别让工具去挤占输入框。
    //
    // 槽位：conversation.input.right（list ⇒ 必须给 id）。
    // 刻意不挂 activity —— 那是 single 槽，官方麦克风已占用，同优先级注册会抛异常。
    //
    // 图标造型也要和官方麦克风一眼可区分：官方是「点一下开始/再点一下停」，
    // 我们是**硬件 PTT**（按住遥控器说话、松手出字），所以画成遥控器而不是麦克风。
    // ------------------------------------------------------------------
    var SLOT_ID = 'dsh-remote-mic';

    var RemoteIcon = function () {
      return h('svg', {
        viewBox: '0 0 24 24', width: 18, height: 18, fill: 'none',
        stroke: 'currentColor', strokeWidth: 1.7, strokeLinecap: 'round', 'aria-hidden': 'true',
      },
        h('rect', { x: 7, y: 2.5, width: 10, height: 19, rx: 2.6 }),
        h('circle', { cx: 12, cy: 7.6, r: 1.9 }),
        h('circle', { cx: 12, cy: 12.8, r: 1.1 }),
        h('circle', { cx: 12, cy: 16.6, r: 1.1 }));
    };

    var PHASE_TEXT = { idle: '未连接', connecting: '连接中', ready: '已连接', error: '出错' };

    /** portal 出去的浮层在首次测量完成前先藏起来（照官方 MEASURE_STYLE 的做法），避免从左上角闪一下。 */
    var MEASURE_STYLE = { position: 'fixed', left: 0, top: 0, visibility: 'hidden' };

    function StatusIcon({ seam: s }) {
      var snap = React.useState(s.state);
      var state = snap[0], setState = snap[1];
      // ⚠️ 必须解构：React.useState 返回的是【数组】，整个数组恒为真。
      //    2026-10-02 的真 bug 就是这里写成 `var open = React.useState(false)` ——
      //    ⇒ 浮层永远渲染（怎么都关不掉），而 setOpen 从未定义（一调用就 ReferenceError 被吞）。
      var openSnap = React.useState(false);
      var open = openSnap[0], setOpen = openSnap[1];
      var root = React.useRef(null);
      var panel = React.useRef(null);

      React.useEffect(function () { return s.subscribe(setState); }, [s]);

      // 开合交给框架，与官方「会话统计」面板（ui-chat 的 useStatDialog）同一套：
      //   ① useAnchoredPosition —— 面板 portal 到 body 后按锚点的视口矩形定位、跟随滚动与缩放、
      //      并夹在视口内（等价于官方 panel 的 style={pos}）
      //   ② useDismissOnOutsidePointer —— pointerdown 落在 root + portal 之外 ⇒ setOpen(false)
      //   ③ Esc —— 官方也是自己挂这一条
      //   ④ 点图标切换
      // 我们不再自己写 document 监听；也不再做「移开鼠标即关」——面板已经 portal 出去，
      // 鼠标从图标移向面板时会离开 root，那样会误关。
      var pos = primitives.useAnchoredPosition({
        open: open,
        anchorRef: root,
        panelRef: panel,
        side: 'top',
        align: 'end',
        gap: 10,
        margin: 8,
      });
      primitives.useDismissOnOutsidePointer(root, open, setOpen, panel);
      React.useEffect(function () {
        if (!open) return;
        var onKey = function (e) { if (e.key === 'Escape') setOpen(false); };
        document.addEventListener('keydown', onKey);
        return function () { document.removeEventListener('keydown', onKey); };
      }, [open]);

      var seconds = (ms) => (ms / 1000).toFixed(1) + ' s';

      var label = state.phase === 'ready'
        ? (state.capturing ? '正在录音 ' + seconds(state.durationMs) : '遥控器就绪')
        : (PHASE_TEXT[state.phase] || '未连接');

      // 试听：把刚收齐的那段 WAV 直接播出来 —— 这是**不依赖 ASR** 就能验"采集路径对不对"的手段
      // （帧头解析、字节累积、WAV 头三样任一错，听感立刻不对）。
      var playClip = function () {
        var clip = state.lastClip;
        if (!clip) return;
        try {
          var url = URL.createObjectURL(new Blob([clip.wav], { type: 'audio/wav' }));
          var audio = new Audio(url);
          var release = function () { URL.revokeObjectURL(url); };
          audio.onended = release;
          audio.onerror = release;
          var started = audio.play();
          if (started && started.catch) started.catch(release);
        } catch (e) { /* 播放失败不影响其它功能 */ }
      };

      var rows = [];
      rows.push(h('dt', null, '状态'), h('dd', null, label + (state.detail ? ' · ' + state.detail : '')));
      if (state.device) {
        var d = state.device;
        rows.push(h('dt', null, '设备'), h('dd', null, d.name || d.model || '—'));
        if (d.battery != null && d.battery >= 0) {
          rows.push(h('dt', null, '电量'), h('dd', null, d.battery + '%'));
        }
        rows.push(h('dt', null, '配对'), h('dd', null, d.paired ? '已配对' : '未配对'));
      }
      if (state.proto != null) rows.push(h('dt', null, '协议'), h('dd', null, 'proto ' + state.proto));
      rows.push(h('dt', null, '接缝'), h('dd', null, s.url()));
      // 刷新键开关：默认随连接事件自动开合（connected 开、断开关），用户也可以手动拨。
      // 手动拨的用途：换台电脑、没带遥控器时也能调试/自证。
      rows.push(h('dt', null, '拦截刷新键'), h('dd', null,
        h(Switch, {
          checked: !!state.interceptRefresh,
          onChange: function (next) { s.setInterceptRefresh(next); },
          label: '拦截刷新键（F5）',
        })));
      rows.push(h('dt', null, '设备状态'), h('dd', null, state.deviceState || '—'));
      if (state.capturing) {
        rows.push(h('dt', null, '本段已收'), h('dd', null, (state.audioBytes / 1024).toFixed(1) + ' KB · ' + seconds(state.durationMs)));
      }
      if (state.lastClip) {
        rows.push(h('dt', null, '上次录音'), h('dd', null,
          seconds(state.lastClip.durationMs) + ' · ' + (state.lastClip.bytes / 1024).toFixed(1) + ' KB'));
      }
      if (state.lastError) rows.push(h('dt', null, '错误'), h('dd', null, state.lastError));

      var panelNode = open ? ReactDOM.createPortal(
        h('div', {
          className: 'dsh-rm-pop',
          ref: panel,
          role: 'dialog',
          'aria-label': '遥控器语音输入',
          // pos 首次测量前是 null ⇒ 先用 MEASURE_STYLE 藏起来（官方同款做法）
          style: pos || MEASURE_STYLE,
        },
          h('h4', null, '遥控器语音输入'),
          h('dl', null, rows),
          // 只有收到过一段录音才显示「试听」；否则整行不渲染（不留空行）
          state.lastClip ? h('div', { className: 'dsh-rm-actions' },
            h('button', { className: 'dsh-rm-link', onClick: playClip }, '试听')) : null),
        document.body) : null;

      return h(React.Fragment, null,
        h('div', { className: 'dsh-rm', ref: root },
          h('button', {
            className: 'dsh-rm-btn',
            'data-phase': state.phase,
            'data-rec': state.capturing ? '1' : '0',
            title: 'dsh-remote-mic · ' + label + '（点击看详情）',
            'aria-label': '遥控器语音输入 · ' + label,
            'aria-expanded': open,
            'aria-haspopup': 'dialog',
            onClick: function () { setOpen(!open); },
          }, h(RemoteIcon))),
        panelNode);
    }

    // ------------------------------------------------------------------
    // 设置页：注册到 plugins.bundle.config 槽位（keyed，key = 包名），
    // 官方 voice-input 就是这么做的 —— 设置进 dsh 自己的设置页，不往输入框里塞控件。
    // ------------------------------------------------------------------
    function SettingsPanel({ seam: s }) {
      var [port, setPort] = React.useState(String(s.port));
      var [saved, setSaved] = React.useState(false);

      var save = function () {
        if (s.setPort(port)) setSaved(true);
        else setPort(String(s.port)); // 非法值：回填，不动连接
      };

      return h('div', { className: 'dsh-rm-set' },
        h('label', { htmlFor: 'dsh-rm-port' }, 'local-mic 接缝端口'),
        h('div', null,
          h('input', {
            id: 'dsh-rm-port', type: 'number', min: '1', max: '65535',
            value: port,
            onChange: function (e) { setPort(e.target.value); setSaved(false); },
          }),
          ' ',
          h('button', { className: 'dsh-rm-link', onClick: save }, '保存')),
        h('div', { style: { fontSize: '11px', opacity: '.6' } },
          '默认 ' + DEFAULT_PORT + '；本机 local-mic 若改过端口（主窗口底部可见），填那个值。'),
        saved ? h('div', { style: { fontSize: '11px', color: '#1D9E75' } }, '已保存，正在重连…') : null);
    }

    // ------------------------------------------------------------------
    // 入口
    // ------------------------------------------------------------------
    var inject = ['slots', 'pluginNavigation'];

    var onOpenSettings = null;   // apply 的 disposer 要能摘掉它，所以提到模块作用域

    function registerUi(ctx) {
      ensureStyle();
      seam.connect();

      // 输入框：只有一个图标
      ctx.slots.inject('conversation.input.right', function () {
        return ctx.slots.register(
          {
            name: 'conversation.input.right',
            id: SLOT_ID,        // list 槽必填
            order: 50,
            inject: function () { return { seam: seam }; },
          },
          StatusIcon
        );
      });

      // 设置：进 dsh 自己的设置页
      ctx.slots.inject('plugins.bundle.config', function () {
        return ctx.slots.register(
          {
            name: 'plugins.bundle.config',
            key: BUNDLE_KEY,    // keyed 槽必填
            inject: function () { return { seam: seam }; },
          },
          SettingsPanel
        );
      });

      // 设置页本身仍注册在 plugins.bundle.config 槽（端口在那里配）。
      // 原先浮层里还有一个「打开设置」按钮走这里跳转；2026-10-02 按用户要求把按钮去掉了，
      // 这条 window 事件通道保留（无副作用），将来若再需要入口可直接复用。
      onOpenSettings = function () {
        try { ctx.pluginNavigation.openBundle(BUNDLE_KEY); } catch (e) { /* 该版本没有这个 API */ }
      };
      window.addEventListener('dsh-remote-mic:open-settings', onOpenSettings);
    }

    async function apply(ctx) {
      var unguard = installRefreshKeyGuard();
      var dispose = ctx.inject(['slots', 'pluginNavigation'], registerUi);
      try {
        await dispose;
      } catch (error) {
        unguard();
        if (onOpenSettings) window.removeEventListener('dsh-remote-mic:open-settings', onOpenSettings);
        seam.dispose();
        throw error;
      }
      return function () {
        unguard();
        if (onOpenSettings) {
          window.removeEventListener('dsh-remote-mic:open-settings', onOpenSettings);
          onOpenSettings = null;
        }
        seam.dispose();
      };
    }

    exports.apply = apply;
    exports.inject = inject;
    return module.exports;
  },
});