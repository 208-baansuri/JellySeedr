(function () {
    const api = '/jellyseedr';
    const styleId = 'seedr-browser-style';
    const state = { modal: null, tree: null, message: null, summary: null, actions: null, loading: null, selection: new Set(), nodes: new Map(), parents: new Map(), expanded: new Set(), fetchBtn: null, deleteBtn: null };

    const h = (x) => Object.assign({ Authorization: 'MediaBrowser Token=' + ApiClient.accessToken() }, x || {});
    const el = (tag, cls, text) => { const node = document.createElement(tag); if (cls) node.className = cls; if (text != null) node.textContent = text; return node; };
    const bytes = (n) => { n = Number(n || 0); if (!n) return '0 B'; const u = ['B', 'KB', 'MB', 'GB', 'TB']; let i = 0; while (n >= 1024 && i < u.length - 1) { n /= 1024; i += 1; } return `${n >= 100 || i === 0 ? n.toFixed(0) : n.toFixed(1)} ${u[i]}`; };

    const explandLessIconHTML = '<svg class="MuiSvgIcon-root MuiSvgIcon-fontSizeMedium css-vubbuv" focusable="false" aria-hidden="true" viewBox="0 0 24 24" data-testid="ExpandLessIcon" style="height: 100%; width: auto; display: block;"><path d="m12 8-6 6 1.41 1.41L12 10.83l4.59 4.58L18 14z"></path></svg>';
    const explandMoreIconHTML = '<svg class="MuiSvgIcon-root MuiSvgIcon-fontSizeMedium css-vubbuv" focusable="false" aria-hidden="true" viewBox="0 0 24 24" data-testid="ExpandMoreIcon" style="height: 100%; width: auto; display: block;"><path d="M16.59 8.59 12 13.17 7.41 8.59 6 10l6 6 6-6z"></path></svg>'

    function css() {
        if (document.getElementById(styleId)) return;
        const s = el('style');
        s.id = styleId;
        s.textContent = `
            .seedr-o{position:fixed;inset:0;display:none;align-items:center;justify-content:center;padding:1rem;background:rgba(0,0,0,.55);z-index:99999}
            .seedr-o.on{display:flex}
            .seedr-p{width:min(980px,100%);height:min(82vh,860px);display:flex;flex-direction:column;overflow:hidden;border-radius:16px;background:var(--theme-background-color,#101010);color:var(--text-primary-color,#fff);box-shadow:0 20px 60px rgba(0,0,0,.45)}
            .seedr-h,.seedr-f{display:flex;align-items:center;justify-content:space-between;gap:1rem;padding:1rem 1.25rem}
            .seedr-h{border-bottom:1px solid rgba(255,255,255,.08)}
            .seedr-f{border-top:1px solid rgba(255,255,255,.08);background:rgba(255,255,255,.02)}
            .seedr-b{position:relative;flex:1;overflow:auto;padding:.75rem .9rem 1rem}
            .seedr-t{display:flex;flex-direction:column;gap:.25rem}
            .seedr-r{display:flex;align-items:center;gap:.55rem;padding:.45rem .5rem;border-radius:12px;cursor:pointer;margin-left:0.35rem;}
            .seedr-r:hover{background:rgba(255,255,255,.05)}
            .seedr-c{cursor:pointer}
            .seedr-n{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
            .seedr-s{flex:0 0 auto;color:var(--secondary-text-color,rgba(255,255,255,.72));font-variant-numeric:tabular-nums}
            .seedr-ch{padding-left:.55rem;border-left:1px solid rgba(255,255,255,.08);margin-left:1.6rem}
            .seedr-ch[hidden]{display:none}
            .seedr-m{display:none;margin-top:.75rem;padding:.8rem .9rem;border-radius:12px;background:rgba(255,255,255,.05);overflow-wrap:anywhere}
            .seedr-m.on{display:block}
            .seedr-x,.seedr-tg{appearance:none;border:0;background:transparent;color:var(--secondary-text-color,rgba(255,255,255,.72));cursor:pointer}
            .seedr-tg{width:1.5rem;padding:0}
            .seedr-tg.p{visibility:hidden}
            .seedr-e{color:var(--secondary-text-color,rgba(255,255,255,.72));padding:.75rem .5rem}
        `;
        document.head.appendChild(s);
    }

    const normFolder = (n) => ({ kind: 'folder', id: n.id || '', parentId: n.parentId || '', name: n.name || '', size: Number(n.size || 0), children: n.children || [], files: n.files || [], path: n.path || '' });
    const normFile = (n) => ({ kind: 'file', id: n.id || '', folderId: n.folderId || '', name: n.name || '', size: Number(n.size || 0), hash: n.hash || '', path: n.path || '' });
    const normTorrent = (n) => ({ kind: 'torrent', id: n.id || '', name: n.name || '', size: Number(n.size || 0), progress: Number(n.progress || 0) });

    function reg(n, parent, kind) {
        let node;
        if (kind === 'torrent') node = normTorrent(n);
        else if (kind === 'folder') node = normFolder(n);
        else node = normFile(n);
        state.nodes.set(node.id, node);
        state.parents.set(node.id, parent);
        return node;
    }

    function setMsg(text, files) {
        if (!state.message) return;
        state.message.innerHTML = '';
        state.message.appendChild(el('div', null, text));
        if (Array.isArray(files) && files.length) {
            const list = el('div');
            list.style.display = 'grid';
            list.style.gap = '.45rem';
            list.style.marginTop = '.65rem';
            files.forEach((f) => { const a = el('a', null, `${f.name} (${bytes(f.size)})`); a.href = f.url; a.target = '_blank'; a.rel = 'noreferrer'; a.style.color = 'var(--theme-primary-color,#00a4dc)'; a.style.textDecoration = 'none'; a.style.wordBreak = 'break-all'; list.appendChild(a); });
            state.message.appendChild(list);
        }
        setTimeout(() => { if (state.message) state.message.classList.remove('on'); }, 2000);
    }

    const clearMsg = () => state.message && (state.message.classList.remove('on'), state.message.replaceChildren());
    const loadState = (on, text) => { if (state.loading) { state.loading.textContent = text || 'Loading...'; state.loading.classList.toggle('on', !!on); } };

    function selected() {
        const out = [];
        state.selection.forEach((id) => {
            const node = state.nodes.get(id);
            if (!node) return;
            for (let p = state.parents.get(id); p; p = state.parents.get(p)) if (state.selection.has(p)) return;
            out.push(node);
        });
        return out;
    }

    function checkpointChangeHandler(event) {
        const checkbox = event.target;
        const itemId = checkbox.dataset.itemId;
        const container = checkbox.parentElement.parentElement;

        const isFolder = container.classList.contains('seeder-folder');

        const isFolderCheckpoint = (checkbox) => checkbox.parentElement.parentElement.classList.contains('seeder-folder');

        const selectionUpdate = (id, checked) => {
            if (checked) {
                state.selection.add(id);
            } else {
                state.selection.delete(id);
            }
        }

        selectionUpdate(itemId, checkbox.checked);

        // Update children nodes if the current node is a folder
        if (isFolder) {
            const childrenContainer = container.nextElementSibling;

            if (childrenContainer.classList.contains('seedr-ch')) {
                const childCheckboxes = childrenContainer.querySelectorAll('.seedr-c');
                childCheckboxes.forEach((childCheckbox) => {
                    childCheckbox.checked = checkbox.checked;
                    selectionUpdate(childCheckbox.dataset.itemId, checkbox.checked);
                });
            }
        }

        let containerFolder;

        if (isFolder && container.parentElement.parentElement.classList.contains('seedr-ch')) {
            containerFolder = container.parentElement.parentElement.parentElement;
        } else if (!isFolder && container.parentElement.classList.contains('seedr-ch')) {
            containerFolder = container.parentElement.parentElement;
        } else {
            containerFolder = null;
        }

        // Update parent nodes on uncheck
        if (!checkbox.checked) {
            while (containerFolder) {
                const containerFolderCheckbox = containerFolder.querySelector('.seedr-c');
                if (containerFolderCheckbox && (containerFolderCheckbox.checked == false)) {
                    break;
                } else {
                    containerFolderCheckbox.checked = false;
                    selectionUpdate(containerFolderCheckbox.dataset.itemId, false);
                    if (containerFolder.parentElement.classList.contains('seedr-ch')) {
                        containerFolder = containerFolder.parentElement.parentElement;
                    } else {
                        containerFolder = null;
                    }
                }
            }
        }

        summary();
    }

    function summary() {
        const items = selected();
        const total = items.reduce((sum, n) => sum + Number(n.size || 0), 0);
        if (!items.length) { state.summary.textContent = 'No items selected'; state.actions.hidden = true; return; }
        state.summary.textContent = `${items.length} selected • ${bytes(total)}`;
        state.actions.hidden = false;

        const hasTorrents = items.some(n => n.kind === 'torrent');
        const hasFilesOrFolders = items.some(n => n.kind === 'folder' || n.kind === 'file');

        if (hasTorrents && !hasFilesOrFolders) {
            if (state.fetchBtn) state.fetchBtn.style.display = 'none';
            if (state.deleteBtn) state.deleteBtn.style.display = '';
        } else {
            if (state.fetchBtn) state.fetchBtn.style.display = '';
            if (state.deleteBtn) state.deleteBtn.style.display = '';
        }
    }

    function clearSelection() {
        state.selection.clear();
        const checkboxes = state.tree.querySelectorAll('.seedr-c');
        checkboxes.forEach((checkbox) => {
            checkbox.checked = false;
        });
    }

    function getCheckBox(itemId) {
        const clb = el('label', 'emby-checkbox-label');
        const cb = el('input', 'seedr-c emby-checkbox'); cb.type = 'checkbox'; cb.dataset.itemId = itemId; cb.onchange = checkpointChangeHandler;
        const emptySpan = el('span', 'checkboxLabel')
        const checkboxOutline = el('span', 'checkboxOutline');
        const checkboxIconChecked = el('span', 'material-icons checkboxIcon checkboxIcon-checked check'); checkboxIconChecked.setAttribute('aria-hidden', 'true');
        const checkboxIconUnchecked = el('span', 'material-icons checkboxIcon checkboxIcon-unchecked'); checkboxIconUnchecked.setAttribute('aria-hidden', 'true');

        checkboxOutline.append(checkboxIconChecked, checkboxIconUnchecked);
        clb.append(cb, emptySpan, checkboxOutline);
        clb.style.width = '1rem'; clb.style.height = '1rem'; clb.style.paddingLeft = '0';
        cb.style.width = '0'; cb.style.height = '0';
        checkboxOutline.style.width = '1rem'; checkboxOutline.style.height = '1rem'; checkboxOutline.style.top = '0';
        clb.onclick = (e) => { e.stopPropagation(); };
        return clb;
    }

    function fileRow(file, depth, parent) {
        const n = reg(file, parent, 'file');
        const row = el('div', 'seedr-r seeder-file');
        const tg = el('button', 'seedr-tg p'); tg.type = 'button'; tg.disabled = true;
        const cb = getCheckBox(n.id);
        row.append(tg, cb, el('div', 'seedr-n', n.name || '(unnamed)'), el('div', 'seedr-s', bytes(n.size)));
        row.onclick = (e) => {
            const input = row.querySelector('input[type="checkbox"]');
            if (e.target !== tg && e.target !== cb && e.target !== input) {
                input.checked = !input.checked;
                input.dispatchEvent(new Event('change'));
            }
        }
        return row;
    }

    function torrentRow(torrent, depth) {
        const n = reg(torrent, null, 'torrent');
        const row = el('div', 'seedr-r seeder-torrent');
        const tg = el('button', 'seedr-tg p'); tg.type = 'button'; tg.disabled = true;
        const cb = getCheckBox(n.id);
        const progressPct = n.progress ? ` (${n.progress}%)` : '';
        const nameText = `[Torrent] ${n.name}${progressPct}`;
        row.append(tg, cb, el('div', 'seedr-n', nameText), el('div', 'seedr-s', bytes(n.size)));
        row.onclick = (e) => {
            const input = row.querySelector('input[type="checkbox"]');
            if (e.target !== tg && e.target !== cb && e.target !== input) {
                input.checked = !input.checked;
                input.dispatchEvent(new Event('change'));
            }
        }
        return row;
    }

    function folderRow(folder, depth, parent) {
        const n = reg(folder, parent, 'folder');
        const wrap = el('div', 'seedr-folder-wrapper');
        const row = el('div', 'seedr-r seeder-folder');
        const tg = el('button', 'seedr-tg'); tg.type = 'button';
        const isExpanded = state.expanded.has(n.id);
        tg.innerHTML = isExpanded ? explandLessIconHTML : explandMoreIconHTML;
        const cb = getCheckBox(n.id);
        row.append(tg, cb, el('div', 'seedr-n', n.name || '(unnamed)'), el('div', 'seedr-s', bytes(n.size)));
        row.onclick = (e) => { if (e.target !== tg && e.target !== cb) { tg.click(); } };
        const children = el('div', 'seedr-ch'); children.hidden = !isExpanded;
        tg.onclick = (e) => {
            e.stopPropagation();
            children.hidden = !children.hidden;
            tg.innerHTML = children.hidden ? explandMoreIconHTML : explandLessIconHTML;
            if (!children.hidden) state.expanded.add(n.id); else state.expanded.delete(n.id);
        };
        (n.children || []).forEach((f) => children.appendChild(folderRow(f, depth + 1, n.id)));
        (n.files || []).forEach((f) => children.appendChild(fileRow(f, depth + 1, n.id)));
        wrap.append(row, children);
        return wrap;
    }

    function render(root) {
        state.tree.replaceChildren();
        if (!root) return state.tree.appendChild(el('div', 'seedr-e', 'No files or folders found in Seedr.'));
        if (root.torrents && root.torrents.length) {
            root.torrents.forEach((t) => state.tree.appendChild(torrentRow(t, 0)));
        }
        const r = normFolder(root);
        (r.children || []).forEach((f) => state.tree.appendChild(folderRow(f, 0, null)));
        (r.files || []).forEach((f) => state.tree.appendChild(fileRow(f, 0, null)));
    }

    async function load() {
        loadState(true, 'Loading Seedr files...');
        try {
            const res = await fetch(`${api}/contents?folderId=0`, { headers: h() });
            const json = await res.json().catch(() => ({}));
            if (!res.ok) throw new Error(json.message || 'Unable to load Seedr files');
            render(json);
            summary();
        } catch (e) {
            state.tree.replaceChildren(el('div', 'seedr-e', e.message || 'Unable to load Seedr files.'));
            state.summary.textContent = 'No items selected';
            state.actions.hidden = true;
        } finally {
            loadState(false);
        }
    }

    async function run(action, label) {
        const items = selected().map((n) => ({ id: n.id, kind: n.kind, name: n.name, size: Number(n.size || 0), path: n.path || '' }));
        if (!items.length) return summary();
        if (action === 'delete' && !confirm(`Delete ${items.length} selected item(s)?`)) return;
        loadState(true, label);
        try {
            const res = await fetch(`${api}/${action}`, { method: 'POST', headers: h({ 'Content-Type': 'application/json' }), body: JSON.stringify({ 'items': items, 'destinationPath': getSelectedLibraryPath() }) });
            const json = await res.json().catch(() => ({}));
            if (!res.ok) throw new Error(json.message || `Seedr ${action} failed`);
            if (action === 'delete') { state.selection.clear(); await load(); clearMsg(); }
            else {
                setMsg(json.message || 'Done', json.files || [])
                setTimeout(clearMsg, 5000);
                clearSelection();
            }
            summary();
        } catch (e) {
            setMsg(e.message || `Seedr ${action} failed`);
        } finally {
            loadState(false);
        }
    }

    function modal() {
        if (state.modal) return;
        css();
        const o = el('div', 'seedr-o');
        const p = el('div', 'seedr-p');
        const h = el('div', 'seedr-h');
        const title = el('div'); title.innerHTML = '<div style="font-size:1.2rem;font-weight:700">Seedr file browser</div><div style="margin-top:.25rem;color:var(--secondary-text-color,rgba(255,255,255,.72))">Select files or folders, then fetch or delete the current selection.</div>';
        // const x = el('button', 'seedr-x', '×'); x.type = 'button'; x.onclick = close;
        const closeButton = document.createElement('button');
        closeButton.className = 'fab emby-button';
        closeButton.type = 'button';
        closeButton.setAttribute('aria-label', 'Close');
        closeButton.innerHTML = '<span class="material-icons close" aria-hidden="true"/>';
        const x = el('button', 'fab emby-button', null); x.type = 'button'; x.setAttribute('aria-label', 'Close');
        const x_icon = el('span', 'material-icons close', null); x_icon.setAttribute('aria-hidden', 'true'); x.appendChild(x_icon);
        x.onclick = close;
        h.append(title, x);
        const b = el('div', 'seedr-b');
        const l = el('div'); l.textContent = 'Loading...'; l.className = 'seedr-e'; l.style.position = 'absolute'; l.style.inset = '0'; l.style.display = 'none'; l.style.alignItems = 'center'; l.style.justifyContent = 'center'; l.style.background = 'rgba(0,0,0,.62)'; l.style.zIndex = '2';
        const t = el('div', 'seedr-t');
        const m = el('div', 'seedr-m');
        b.append(l, t, m);
        const f = el('div', 'seedr-f');
        const s = el('div', null, 'No items selected');
        const a = el('div'); a.hidden = true;
        const fb = el('button', 'raised button-submit emby-button', 'Fetch Files'); fb.type = 'button'; fb.onclick = () => run('fetch', 'Fetching files...');
        const db = el('button', 'button-flat emby-button', 'Delete'); db.type = 'button'; db.onclick = () => run('delete', 'Deleting files...');
        a.append(fb, db);
        f.append(s, a);
        p.append(h, b, f); o.append(p); document.body.appendChild(o);
        o.onclick = (e) => { if (e.target === o) close(); };
        document.addEventListener('keydown', (e) => { if (e.key === 'Escape') close(); });
        state.modal = o; state.tree = t; state.message = m; state.summary = s; state.actions = a; state.loading = l;
        state.fetchBtn = fb; state.deleteBtn = db;
    }

    function open() {
        modal();
        state.modal.classList.add('on');
        state.selection.clear(); state.nodes.clear(); state.parents.clear();
        state.summary.textContent = 'Loading Seedr files...';
        state.actions.hidden = true; clearMsg(); state.tree.replaceChildren();
        load();
    }

    function close() { if (state.modal) state.modal.classList.remove('on'); }
    function bind() { const b = document.getElementById('seedr_browse_files_btn'); if (!b) return false; b.onclick = open; return true; }
    if (!bind()) document.addEventListener('DOMContentLoaded', bind);
})();
