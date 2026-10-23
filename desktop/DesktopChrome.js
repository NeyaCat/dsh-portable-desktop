(() => {
  if (window !== window.top || location.hostname !== '127.0.0.1') return;
  const send = command => window.chrome.webview.postMessage(`desktop:${command}`);
  const prepare = () => {
    const root = document.documentElement;
    root.setAttribute('data-windows-titlebar', '');
    root.style.setProperty('--dsh-windows-titlebar-height', '28px');
  };
  // Set the layout flag before the DSH application mounts.
  if (document.documentElement) prepare();
  document.addEventListener('DOMContentLoaded', () => {
    prepare();
    // Do not expose a blank document between navigation and React mounting.
    const started = Date.now();
    let stable = 0, finished = false;
    const mounted = setInterval(() => {
      const root = document.getElementById('root');
      // Use the actual application root, not a skin's private CSS class.
      const boot = root && root.querySelector('[data-dsh-boot]');
      const visible = root && [...root.querySelectorAll('main,nav,aside,[role=main],[role=dialog],button,textarea,input,[class*="_frame"]')].filter(node => {
        const rect = node.getBoundingClientRect();
        return rect.height > 12 && rect.width > 12 && getComputedStyle(node).visibility !== 'hidden';
      }).length >= 2;
      if (visible && !boot) stable++; else stable = 0;
      if (stable >= 3 && !finished) {
        finished = true; clearInterval(mounted);
        requestAnimationFrame(() => requestAnimationFrame(() => {
          send('ui-ready'); exportTheme(); setTimeout(exportTheme, 800);
        }));
      } else if (Date.now() - started > 40000 && !finished) {
        finished = true; clearInterval(mounted);
        send('startup-error:应用根节点未完成挂载；请检查插件加载错误。');
      }
    }, 100);
    window.addEventListener('pagehide', () => clearInterval(mounted), {once:true});
    const style = document.createElement('style');
    style.textContent = `
      #dsh-desktop-chrome {
        position:fixed; inset:0 0 auto; height:var(--dsh-windows-titlebar-height); z-index:2147483646;
        display:flex; align-items:center; background:transparent !important;
        color:var(--dsw-alias-label-primary, inherit);
        user-select:none; font:12px "Segoe UI","Microsoft YaHei UI",sans-serif;
      }
      /* The DSH skin owns the frame and its ::before paint, including wallpaper
         opacity composition. Our chrome adds no background layer on top. */
      #dsh-desktop-chrome .desktop-drag {
        flex:1; min-width:0; align-self:stretch; display:flex; align-items:center;
      }
      #dsh-desktop-title { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
      #dsh-desktop-icon { width:16px; height:16px; margin:0 10px 0 12px; flex:none; cursor:default; }
      #dsh-desktop-icon[data-tone="white"] { filter:brightness(0) invert(1); }
      #dsh-desktop-icon[data-tone="black"] { filter:brightness(0); }
      /* The Windows profile fixes these controls into the caption row.
         Keep their DSH event handlers, but expose them through the icon menu
         instead of leaving clickable controls hidden behind our title. */
      html[data-windows-titlebar] [class*="_logoRow"] [class*="_toggle"],
      html[data-windows-titlebar] [class*="_topStrip"] [class*="_toggle"],
      html[data-windows-titlebar] [class*="_collapsed"] [class*="_newSession"] {
        display:none !important;
      }
      #dsh-desktop-chrome button {
        all:unset; box-sizing:border-box; width:42px; height:100%; display:grid;
        place-items:center; color:inherit; cursor:default;
      }
      #dsh-desktop-chrome button:hover { background:color-mix(in srgb,currentColor 12%,transparent); }
      #dsh-desktop-chrome button:focus-visible { outline:2px solid currentColor; outline-offset:-3px; }
      #dsh-desktop-chrome button:last-child:hover { background:#c42b1c; color:white; }
      #dsh-desktop-chrome svg { width:12px; height:12px; fill:none; stroke:currentColor; stroke-width:1; }
      .dsh-desktop-edge { position:fixed; z-index:2147483647; }
    `;
    document.head.append(style);
    const bar = document.createElement('div');
    bar.id = 'dsh-desktop-chrome';
    const icon = document.createElement('img');
    icon.id = 'dsh-desktop-icon';
    icon.src = '__DSH_ICON_DATA__';
    icon.alt = 'DeepSeek Harness';
    icon.title = '右键打开操作菜单';
    icon.tabIndex = 0;
    icon.draggable = false;
    // Both variants keep the ICO's original alpha/antialiasing. No second,
    // potentially mismatched silhouette is needed for the light version.
    icon.dataset.tone = 'white';
    icon.addEventListener('contextmenu', e => {
      e.preventDefault();
      e.stopPropagation();
      send('menu');
    });
    icon.addEventListener('keydown', e => {
      if (e.key === 'ContextMenu' || (e.shiftKey && e.key === 'F10')) { e.preventDefault(); send('menu'); }
    });
    bar.append(icon);
    const drag = document.createElement('div');
    drag.className = 'desktop-drag';
    const title = document.createElement('span');
    title.id = 'dsh-desktop-title';
    drag.append(title);
    // DSH's DocumentTitle already tracks the selected session, even when the
    // sidebar is collapsed. Observe that projection rather than truncated rows.
    const updateTitle = () => {
      const source = document.title.trim();
      const separator = source.lastIndexOf(' — ');
      const session = separator >= 0 ? source.slice(0, separator).trim() : '';
      const value = 'DeepSeek Harness' + (session ? ` - ${session}` : '');
      if (title.textContent === value) return;
      title.textContent = value;
      title.title = value;
      send(`title:${value}`);
    };
    new MutationObserver(updateTitle).observe(document.head, { childList: true, subtree: true, characterData: true });
    updateTitle();
    drag.addEventListener('mousedown', e => { if (e.button === 0 && e.detail === 1) send('drag'); });
    drag.addEventListener('dblclick', () => send('maximize'));
    bar.append(drag);
    for (const [command, label, path] of [
      ['minimize', '最小化', 'M1 6h10'],
      ['maximize', '最大化 / 还原', 'M1.5 1.5h9v9h-9z'],
      ['close', '关闭（退出 / 最小化）', 'M1 1l10 10M11 1L1 11']
    ]) {
      const button = document.createElement('button');
      button.title = label;
      button.dataset.command = command;
      button.setAttribute('aria-label', label);
      button.innerHTML = `<svg viewBox="0 0 12 12" aria-hidden="true"><path d="${path}"/></svg>`;
      button.addEventListener('click', () => send(command));
      bar.append(button);
    }
    document.body.append(bar);
    // Nonmodal update notification: never steals focus or covers the application.
    // All server/package strings go through textContent, never HTML.
    const toastStyle = document.createElement('style');
    toastStyle.textContent = `
      .dsh-update-toast{position:fixed;right:16px;top:calc(var(--dsh-windows-titlebar-height) + 12px);
        z-index:2147483645;box-sizing:border-box;width:min(370px,calc(100vw - 32px));padding:14px;
        background:rgba(24,22,29,.88);color:#faf8ff;
        backdrop-filter:blur(24px) saturate(145%);-webkit-backdrop-filter:blur(24px) saturate(145%);
        isolation:isolate;text-shadow:none;
        border:1px solid var(--dsw-alias-border-l3,#777);border-radius:12px;
        box-shadow:0 8px 32px #0004;font:13px/1.6 "Segoe UI","Microsoft YaHei UI",sans-serif}
      .dsh-update-toast[data-scheme="light"]{background:rgba(250,249,253,.9);color:#1b1724;border-color:#29203330}
      .dsh-update-toast[hidden]{display:none}
      .dsh-update-toast button:hover{background:#8883}
      .dsh-update-toast button:focus-visible{outline:2px solid currentColor;outline-offset:2px}
      @supports not (backdrop-filter:blur(1px)){.dsh-update-toast{background:#201d27}.dsh-update-toast[data-scheme="light"]{background:#faf9fd}}
      .dsh-update-toast p{margin:0 20px 10px 0}
      .dsh-update-toast button{cursor:pointer;font:inherit;color:inherit;border:1px solid #8886;
        border-radius:6px;background:transparent;padding:4px 10px;margin-right:8px}
      .dsh-update-toast [data-close]{position:absolute;right:5px;top:4px;border:0;margin:0;padding:2px 8px}
    `;
    document.head.append(toastStyle);
    const toast = document.createElement('aside');
    toast.id = 'dsh-update-toast'; toast.className = 'dsh-update-toast'; toast.hidden = true; toast.setAttribute('role', 'status');
    toast.setAttribute('aria-live', 'polite');
    const updateText = document.createElement('p');
    const acceptUpdate = document.createElement('button'); acceptUpdate.textContent = '后台安装';
    acceptUpdate.onclick = () => { acceptUpdate.disabled = true; send('update-install'); };
    const restartUpdate = document.createElement('button'); restartUpdate.textContent = '重启'; restartUpdate.hidden = true;
    restartUpdate.id = 'dsh-update-restart';
    restartUpdate.onclick = () => { restartUpdate.disabled = true; send('update-restart'); };
    const ignoreUpdate = document.createElement('button'); ignoreUpdate.textContent = '忽略此版本'; ignoreUpdate.id='dsh-update-ignore';
    ignoreUpdate.onclick=()=>{ignoreUpdate.disabled=true;send('update-ignore')};
    const dismissUpdate = document.createElement('button'); dismissUpdate.textContent = '×';
    dismissUpdate.dataset.close = ''; dismissUpdate.setAttribute('aria-label', '关闭更新提示');
    dismissUpdate.onclick = () => { toast.hidden = true; };
    toast.append(updateText, acceptUpdate, restartUpdate, ignoreUpdate, dismissUpdate); document.body.append(toast);
    const pluginToast = document.createElement('aside'); pluginToast.className = 'dsh-update-toast';
    pluginToast.id = 'dsh-plugin-toast'; pluginToast.hidden = true; pluginToast.setAttribute('role','status');
    pluginToast.style.top = 'calc(var(--dsh-windows-titlebar-height) + 150px)';
    const pluginText = document.createElement('p');
    const pluginClose = document.createElement('button'); pluginClose.textContent='知道了';
    pluginClose.onclick=()=>{pluginToast.hidden=true}; pluginToast.append(pluginText,pluginClose);document.body.append(pluginToast);
    // Resolve the skin's actual computed background, including translucent
    // ancestors and the caption pseudo-element. CSS color parsing is delegated
    // to Chromium so rgb(), color-mix() and color(srgb ...) all work.
    const sampleCanvas = document.createElement('canvas');
    sampleCanvas.width = sampleCanvas.height = 1;
    const sample = sampleCanvas.getContext('2d', { willReadFrequently: true });
    const rgba = color => {
      sample.clearRect(0, 0, 1, 1);
      sample.fillStyle = color;
      sample.fillRect(0, 0, 1, 1);
      return [...sample.getImageData(0, 0, 1, 1).data].map((v, i) => i === 3 ? v / 255 : v);
    };
    const over = (base, color) => {
      const alpha = color[3] + base[3] * (1 - color[3]);
      return alpha === 0 ? [0, 0, 0, 0] : [
        ...color.slice(0, 3).map((v, i) => (v * color[3] + base[i] * base[3] * (1 - color[3])) / alpha),
        alpha
      ];
    };
    const luminance = color => color.slice(0, 3).map(v => {
      const s = v / 255;
      return s <= 0.04045 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
    }).reduce((sum, value, i) => sum + value * [0.2126, 0.7152, 0.0722][i], 0);
    const updateIconTone = () => {
      const frame = document.querySelector('[class*="_frame"][data-sidebar-collapsed], [class*="_frame"]:has([class*="_sidebarCol"])');
      const layers = [];
      for (let element = frame || document.body; element; element = element.parentElement)
        layers.unshift(getComputedStyle(element).backgroundColor);
      if (frame) layers.push(getComputedStyle(frame, '::before').backgroundColor);
      let background = layers.reduce((base, color) => over(base, rgba(color)), [0, 0, 0, 0]);
      // A wallpaper/gradient cannot be read as pixels through getComputedStyle.
      // Use the skin's foreground contrast when its color layers are transparent.
      if (background[3] < 0.5) {
        const lightForeground = luminance(rgba(getComputedStyle(bar).color)) > 0.179;
        background = lightForeground ? [0, 0, 0, 1] : [255, 255, 255, 1];
      }
      const lightness = luminance(background);
      const tone = (1.05 / (lightness + 0.05)) >= ((lightness + 0.05) / 0.05) ? 'white' : 'black';
      if (icon.dataset.tone !== tone) icon.dataset.tone = tone;
    };
    // Check cheap computed colors rather than observing every streaming chat
    // mutation. This also catches theme stylesheet swaps and CSS transitions.
    updateIconTone();
    // Export only resolved style data, never page content or credentials.
    // The host caches this snapshot so the version manager, splash and exit
    // prompt open in the live skin instead of a hard-coded palette.
    let previousTheme = '';
    const exportTheme = () => {
      const style = getComputedStyle(document.body);
      const probe = document.createElement('span');
      document.body.append(probe);
      const theme = {};
      for (const [name, token, fallback] of [
        ['bg','bg-base','#131116'], ['surface','bg-layer-1','#1d1b20'],
        ['surface2','bg-layer-2','#2c2c2e'], ['surface3','bg-layer-3','#353638'],
        ['text','label-primary',style.color], ['muted','label-secondary','#aaa'],
        ['tertiary','label-tertiary','#81858c'],
        ['accent','brand-primary',style.color], ['soft','brand-primary-soft','transparent'],
        ['onAccent','brand-text','transparent'], ['border','border-l2','#8884'],
        ['hover','interactive-bg-hover','#8882'], ['focus','brand-primary','#8888'],
        ['danger','state-error-primary','#ef675c'], ['success','state-success-primary','#6ed889'],
        ['warn','state-warn-primary','#fac053']]) {
        probe.style.color = `var(--dsw-alias-${token}, ${fallback})`;
        theme[name] = getComputedStyle(probe).color;
      }
      // Geometry, depth and type come from the skin's own tokens, so the other
      // windows reuse its corners, shadows and font instead of guessing.
      try {
        const metric = (property, token, fallback) => {
          probe.style[property] = `var(--dsw-${token}, ${fallback})`;
          const value = getComputedStyle(probe)[property];
          probe.style[property] = '';
          return value;
        };
        theme.radiusSm = metric('borderTopLeftRadius', 'radius-sm', '8px');
        theme.radius = metric('borderTopLeftRadius', 'radius-md', '12px');
        theme.radiusLg = metric('borderTopLeftRadius', 'radius-lg', '16px');
        theme.shadow = metric('boxShadow', 'shadow-lv2', 'none');
        theme.font = style.fontFamily;
        theme.fontSize = style.fontSize;
      } catch (error) { /* A missing metric keeps the default geometry. */ }
      probe.remove();
      // A standalone canvas uses the skin's opaque tint beneath translucent panels.
      const tint = rgba(theme.bg);
      theme.bg = `rgb(${tint.slice(0,3).map(Math.round).join(',')})`;
      // A skin may only swap its brand colour; derive the tint and label colour
      // from it so a primary fill never lands on the wrong foreground.
      const accent = rgba(theme.accent);
      if (rgba(theme.soft)[3] < 0.02)
        theme.soft = `rgba(${accent.slice(0,3).map(Math.round).join(',')}, 0.16)`;
      if (rgba(theme.onAccent)[3] < 0.02)
        theme.onAccent = luminance(accent) > 0.5 ? 'rgb(16,14,20)' : 'rgb(255,255,255)';
      theme.scheme = luminance(tint) > .179 ? 'light' : 'dark';
      // A wallpaper that exists only as a painted gradient can travel as data;
      // canvas, image and video layers fall back to the skin tint above.
      try {
        const frame = document.querySelector('[class*="_frame"]') || document.body;
        const paint = getComputedStyle(frame, '::before').backgroundImage;
        theme.wallpaper = /^(repeating-)?(linear|radial|conic)-gradient\(/.test(paint) && paint.length < 900 ? paint : 'none';
      } catch (error) { theme.wallpaper = 'none'; }
      toast.dataset.scheme=pluginToast.dataset.scheme=theme.scheme;
      const encoded = JSON.stringify(theme);
      if (encoded !== previousTheme) { previousTheme = encoded; send('theme:' + encoded); }
    };
    exportTheme();
    const themeTimer = setInterval(exportTheme, 1500);
    window.addEventListener('pagehide', () => clearInterval(themeTimer), { once: true });
    const toneTimer = setInterval(updateIconTone, 350);
    window.addEventListener('pagehide', () => clearInterval(toneTimer), { once: true });
    window.chrome.webview.addEventListener('message', e => {
      if (e.data.type === 'plugin-reminder') { pluginText.textContent=e.data.text;pluginToast.hidden=false; }
      if (e.data.type === 'update') {
        if(e.data.state==='ignored'){toast.hidden=true;return;}
        updateText.textContent = e.data.text;
        ignoreUpdate.hidden=e.data.state!=='available'; ignoreUpdate.disabled=false;
        acceptUpdate.hidden = e.data.state !== 'available';
        restartUpdate.hidden = e.data.state !== 'ready';
        restartUpdate.disabled = false;
        acceptUpdate.disabled = false;
        toast.hidden = false;
      }
      if (e.data.type === 'window-state') {
        const maximized = e.data.maximized;
        document.documentElement.toggleAttribute('data-desktop-maximized', maximized);
        const button = bar.querySelector('[data-command="maximize"]');
        button.title = maximized ? '还原' : '最大化';
        button.setAttribute('aria-label', button.title);
        button.querySelector('path').setAttribute('d', maximized ? 'M3.5 1.5h7v7M1.5 3.5h7v7h-7z' : 'M1.5 1.5h9v9h-9z');
      }
    });
    // WebView owns pointer input, so edge grips explicitly delegate native sizing.
    const edges = [
      ['left:0;top:6px;bottom:6px;width:5px', 'ew-resize'],
      ['right:0;top:6px;bottom:6px;width:5px', 'ew-resize'],
      ['top:0;left:6px;right:6px;height:5px', 'ns-resize'],
      ['top:0;left:0;width:6px;height:6px', 'nwse-resize'],
      ['top:0;right:0;width:6px;height:6px', 'nesw-resize'],
      ['bottom:0;left:6px;right:6px;height:5px', 'ns-resize'],
      ['bottom:0;left:0;width:6px;height:6px', 'nesw-resize'],
      ['bottom:0;right:0;width:6px;height:6px', 'nwse-resize']
    ];
    edges.forEach(([position, cursor], i) => {
      const grip = document.createElement('div');
      grip.className = 'dsh-desktop-edge';
      grip.style.cssText = `${position};cursor:${cursor}`;
      grip.addEventListener('mousedown', e => { if (e.button === 0) { e.preventDefault(); send(`resize:${i + 1}`); } });
      document.body.append(grip);
    });
  }, { once: true });
})();
