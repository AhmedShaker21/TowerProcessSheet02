// Operations editor + live route-card preview.
// The preview generator mirrors OperationCatalog.Generate in C# so the user
// sees exactly what will be produced (material, inspections, galvanizing, etc.).
(function () {
    var catalogEl = document.getElementById('opCatalog');
    var opsBody = document.getElementById('opsBody');
    var tpl = document.getElementById('opTemplate');
    var addBtn = document.getElementById('addOp');
    var previewBody = document.getElementById('previewBody');
    if (!catalogEl || !opsBody || !tpl || !addBtn) return;

    var defsList = [];
    try { defsList = JSON.parse(catalogEl.textContent || '[]'); } catch (e) { }
    var defs = {};
    defsList.forEach(function (d) { defs[d.key] = d; });

    var get = function (name) { return document.querySelector('[name="' + name + '"]'); };
    var partNo = get('PartNo'), drawingNo = get('DrawingNo'), project = get('ProjectName');
    var section = get('Section'), cutLength = get('CutLength'), material = get('Material'),
        galv = get('IncludeGalvanizing');

    // ----- "Other…" for Section and Material: pick it and type a value that is not in the list -----
    var OTHER = '__other__';
    function normSection(v) {
        return (v || '').toUpperCase().replace(/\s+/g, '').replace(/(\d)[X\u00D7](\d)/g, '$1*$2');
    }
    function normMaterial(v) { return (v || '').trim().replace(/\s+/g, ' ').toUpperCase(); }

    // Adds (or selects) an option for a value that came from outside the list (a drawing, the DB, the user).
    function ensureOption(sel, val, custom) {
        if (!sel || !val) return;
        var found = null;
        Array.prototype.forEach.call(sel.options, function (o) { if (o.value === val) found = o; });
        if (!found) {
            found = document.createElement('option');
            found.value = val; found.textContent = val;
            if (custom) found.setAttribute('data-custom', '1');
            var other = sel.querySelector('option[value="' + OTHER + '"]');
            if (other) sel.insertBefore(found, other); else sel.appendChild(found);
        }
        sel.value = val;
    }

    function setupCustom(sel, label, placeholder, normalize) {
        if (!sel) return null;
        var box = document.createElement('input');
        box.type = 'text'; box.className = 'input'; box.placeholder = placeholder;
        box.autocomplete = 'off'; box.style.display = 'none'; box.style.marginTop = '6px';
        sel.parentNode.insertBefore(box, sel.nextSibling);

        function addOther() {
            var o = sel.querySelector('option[value="' + OTHER + '"]');
            if (!o) { o = document.createElement('option'); o.value = OTHER; o.textContent = label; }
            sel.appendChild(o);             // always last
        }
        function dropCustom() {
            Array.prototype.slice.call(sel.querySelectorAll('option[data-custom]')).forEach(function (o) { o.remove(); });
        }
        sel.addEventListener('change', function () {
            if (sel.value === OTHER) { box.style.display = ''; box.focus(); }
            else { box.style.display = 'none'; box.value = ''; dropCustom(); }
        });
        box.addEventListener('input', function () {
            var v = normalize(box.value);
            dropCustom();
            if (v) ensureOption(sel, v, true); else sel.value = OTHER;
            renderPreview();
        });
        if (sel.form) sel.form.addEventListener('submit', function () {
            if (sel.value === OTHER) sel.value = '';       // "Other" chosen but nothing typed
        });
        addOther();
        return { refresh: function () { box.style.display = 'none'; box.value = ''; addOther(); } };
    }

    // ----- profile (angle / plate) + section dropdown -----
    var profileSel = document.getElementById('profileType');
    var sectionSel = document.getElementById('sectionSelect');
    var sectionData = {};
    try { sectionData = JSON.parse(document.getElementById('sectionData').textContent || '{}'); } catch (e) { }

    function isPlate(v) { return /^\d+\*\d+\*\d+$/.test((v || '').trim()); }
    var sectionCustom = setupCustom(sectionSel, '\u270E Other \u2014 type it\u2026', 'e.g. L65*6 or 1000*3000*20', normSection);

    function fillSections(profile, selectValue) {
        if (!sectionSel) return;
        var list = (profile === 'plate' ? sectionData.plate : sectionData.angle) || [];
        var items = list.slice();
        if (selectValue && items.indexOf(selectValue) === -1) items.unshift(selectValue); // keep stored value
        sectionSel.innerHTML = '';
        items.forEach(function (s) {
            var o = document.createElement('option');
            o.value = s; o.textContent = s;
            if (s === selectValue) o.selected = true;
            sectionSel.appendChild(o);
        });
        if (sectionCustom) sectionCustom.refresh();
    }

    if (sectionSel) {
        var cur = sectionSel.getAttribute('data-current') || '';
        var profile = isPlate(cur) ? 'plate' : 'angle';
        if (profileSel) profileSel.value = profile;
        fillSections(profile, cur);
        sectionSel.addEventListener('change', function () { renderPreview(); });
    }
    if (profileSel) {
        profileSel.addEventListener('change', function () {
            fillSections(profileSel.value, null);
            renderPreview();
        });
    }

    setupCustom(material, '\u270E Other \u2014 type it\u2026', 'e.g. S460 / ST.60', normMaterial);

    // ----- generator (kept in sync with the C# version) -----
    function expandSection(s) {
        if (!s) return '';
        s = s.trim();
        if (/^l/i.test(s)) s = s.slice(1);
        var parts = s.split('*').map(function (x) { return x.trim(); }).filter(Boolean);
        if (parts.length === 2) return parts[0] + '*' + parts[0] + '*' + parts[1];
        return parts.join('*');
    }
    function splitMaterial(m) {
        if (!m) return ['', ''];
        var i = m.indexOf('/');
        return i < 0 ? [m.trim(), ''] : [m.slice(0, i).trim(), m.slice(i + 1).trim()];
    }
    function pad(n) { n = String(n); while (n.length < 3) n = '0' + n; return n; }

    function generate() {
        var h = {
            drawing: drawingNo ? drawingNo.value : '',
            project: project ? project.value : '',
            section: section ? section.value : '',
            cutLength: cutLength ? cutLength.value : '',
            material: material ? material.value : '',
            galv: galv ? galv.checked : false,
        };
        var rows = [], op = 0;
        var next = function () { op += 10; return pad(op); };

        var gm = splitMaterial(h.material);
        var sectionVal = h.section || '';
        var matLine = isPlate(sectionVal)
            ? 'MATERIAL  ' + gm[0] + ' / ' + gm[1] + '\n' + 'Plate  ' + sectionVal
            : 'MATERIAL  ' + gm[0] + ' / ' + gm[1] + '\n' + 'Angle  ' + expandSection(sectionVal) + '   L= 12000mm';
        var ccMaterial = fieldVal('CustomMaterialCostCenter');
        var ccIncomingInsp = fieldVal('CustomIncomingInspCostCenter');
        var ccFinalInsp = fieldVal('CustomFinalInspCostCenter');
        var ccFinalStore = fieldVal('CustomFinalStoreCostCenter');
        rows.push({ cc: ccMaterial || '4730', s: 'W', t: 'W', op: '', d: matLine,
            ccTarget: { name: 'CustomMaterialCostCenter' }, ccRaw: ccMaterial });
        rows.push({ cc: ccIncomingInsp || '5110', s: 'W', t: 'W', op: next(), insp: true,
            d: 'Insp\nCheck the incoming Material and Dimension',
            ccTarget: { name: 'CustomIncomingInspCostCenter' }, ccRaw: ccIncomingInsp });

        var opRowsList = opRows();
        if (h.galv && !opRowsList.some(function (r) { return r.key === 'GALVANIZE'; }))
            opRowsList.push({ key: 'GALVANIZE', customTime: '' });

        opRowsList.forEach(function (r) {
            var def = defs[r.key]; if (!def) return;
            var d = def.pattern
                .split('{length}').join(h.cutLength || '')
                .split('{project}').join(h.project || '')
                .split('{drawing}').join(h.drawing || '');
            if (r.customDescription) d = r.customDescription;
            var t = r.customTime ? r.customTime : def.timeAllowed;
            var cc = r.customCostCenter ? r.customCostCenter : def.costCenter;
            var s = r.customSetting ? r.customSetting : def.setting;
            var ccInsp = r.customInspCostCenter ? r.customInspCostCenter : (def.inspectionCostCenter || '5130');
            rows.push({ cc: cc, s: s, t: t, op: next(), d: d, opIndex: r.idx,
                ccRaw: r.customCostCenter || '', sRaw: r.customSetting || '', tRaw: r.customTime || '',
                ccTarget: { opIdx: r.idx, prop: 'CustomCostCenter' } });
            rows.push({ cc: ccInsp, s: 'W', t: 'W', op: '', insp: true, d: 'INSP.',
                ccRaw: r.customInspCostCenter || '', ccTarget: { opIdx: r.idx, prop: 'CustomInspCostCenter' } });
        });

        rows.push({ cc: ccFinalInsp || '5150', s: 'W', t: 'W', op: next(), insp: true, d: 'F. Insp.',
            ccTarget: { name: 'CustomFinalInspCostCenter' }, ccRaw: ccFinalInsp });
        rows.push({ cc: ccFinalStore || '4411', s: 'W', t: 'W', op: next(), d: 'F. Store',
            ccTarget: { name: 'CustomFinalStoreCostCenter' }, ccRaw: ccFinalStore });
        return rows;
    }

    function fieldVal(name) {
        var el = document.querySelector('[name="' + name + '"]');
        return el ? el.value.trim() : '';
    }

    function opRows() {
        return Array.prototype.map.call(
            opsBody.querySelectorAll('.op-row'),
            function (row) {
                var sel = row.querySelector('.op-key');
                var idxInput = row.querySelector('input[name="Operations.Index"]');
                var timeInput = row.querySelector('[name*="CustomTimeAllowed"]');
                var cc = row.querySelector('[name*="CustomCostCenter"]');
                var st = row.querySelector('[name*="CustomSetting"]');
                var ds = row.querySelector('[name*="CustomDescription"]');
                var ic = row.querySelector('[name*="CustomInspCostCenter"]');
                return {
                    key: sel ? sel.value : '',
                    idx: idxInput ? idxInput.value : '',
                    customTime: timeInput ? timeInput.value.trim() : '',
                    customCostCenter: cc ? cc.value.trim() : '',
                    customSetting: st ? st.value.trim() : '',
                    customDescription: ds ? ds.value.trim() : '',
                    customInspCostCenter: ic ? ic.value.trim() : ''
                };
            }
        ).filter(function (r) { return r.key; });
    }

    var fieldMap = { cc: 'CustomCostCenter', s: 'CustomSetting', t: 'CustomTimeAllowed', d: 'CustomDescription' };

    var costCenterEl = document.getElementById('costCenterCatalog');
    var costCenterList = [];
    try { costCenterList = JSON.parse(costCenterEl ? costCenterEl.textContent : '[]') || []; } catch (e) { }
    var presetValues = ['0', '5', '10', '15', '20', '25', '30'];   // "0" prints as W on the sheet
    if (!document.getElementById('presetNumbers')) {
        var dl = document.createElement('datalist');
        dl.id = 'presetNumbers';
        presetValues.forEach(function (v) {
            var opt = document.createElement('option');
            opt.value = v;
            opt.label = v === '0' ? '0 — prints as W' : v;
            dl.appendChild(opt);
        });
        document.body.appendChild(dl);
    }

    function renderPreview() {
        if (!previewBody) return;
        previewBody.innerHTML = '';
        generate().forEach(function (r) {
            var tr = document.createElement('tr');
            if (r.insp) tr.className = 'insp';
            var editable = r.opIndex !== undefined && r.opIndex !== null && r.opIndex !== '';

            function textCell(cls, field, val) {
                var td = document.createElement('td');
                td.className = cls;
                td.textContent = displayVal(field, val);
                if (editable) {
                    td.contentEditable = 'true';
                    td.dataset.opIdx = r.opIndex;
                    td.dataset.field = field;
                    td.classList.add('editable-cell');
                    td.title = 'Click to edit';
                }
                return td;
            }

            // Every row's Cost Center is a dropdown; Setting/Time stay dropdowns on the
            // real operation row. `target` says which hidden field a pick writes to:
            // {opIdx, prop} for a per-operation field, or {name} for a sheet-level field.
            function selectCell(target, val, options, defaultLabel) {
                var td = document.createElement('td');
                td.className = 'c';
                if (!target) { td.textContent = displayVal('cc', val); return td; }
                var sel = document.createElement('select');
                sel.className = 'cell-select';
                if (target.opIdx !== undefined) { sel.dataset.opIdx = target.opIdx; sel.dataset.prop = target.prop; }
                else { sel.dataset.sheetField = target.name; }
                var blank = document.createElement('option');
                blank.value = ''; blank.textContent = defaultLabel;
                sel.appendChild(blank);
                var found = false;
                options.forEach(function (o) {
                    var opt = document.createElement('option');
                    opt.value = o.value; opt.textContent = o.label;
                    if (o.value === val) { opt.selected = true; found = true; }
                    sel.appendChild(opt);
                });
                if (!found && val) {
                    var extra = document.createElement('option');
                    extra.value = val; extra.textContent = val + ' (current)'; extra.selected = true;
                    sel.appendChild(extra);
                }
                td.appendChild(sel);
                return td;
            }

            function displayVal(field, val) {
                if ((field === 's' || field === 't') && val === '0') return 'W';
                return val;
            }

            // Setting/Time: a real text input (so any number can be typed), with the
            // presets offered as suggestions via a native datalist dropdown.
            function comboCell(opIdxVal, prop, field, val, placeholderVal) {
                var td = document.createElement('td');
                td.className = 'c';
                var input = document.createElement('input');
                input.type = 'text';
                input.className = 'cell-combo';
                input.setAttribute('list', 'presetNumbers');
                input.placeholder = displayVal(field, placeholderVal) + ' (default)';
                input.value = val || '';
                input.dataset.opIdx = opIdxVal;
                input.dataset.prop = prop;
                td.appendChild(input);
                return td;
            }

            var ccOptions = costCenterList.map(function (c) { return { value: c.number, label: c.name + ' (' + c.number + ')' }; });

            tr.appendChild(selectCell(r.ccTarget, r.ccRaw !== undefined ? r.ccRaw : '', ccOptions, '(default — ' + r.cc + ')'));
            if (editable) {
                tr.appendChild(comboCell(r.opIndex, '__s__', 's', r.sRaw, r.s));
                tr.appendChild(comboCell(r.opIndex, '__t__', 't', r.tRaw, r.t));
            } else {
                var tds = document.createElement('td'); tds.className = 'c'; tds.textContent = displayVal('s', r.s); tr.appendChild(tds);
                var tdt = document.createElement('td'); tdt.className = 'c'; tdt.textContent = displayVal('t', r.t); tr.appendChild(tdt);
            }
            var opTd = document.createElement('td'); opTd.className = 'c'; opTd.textContent = r.op; tr.appendChild(opTd);
            tr.appendChild(textCell('d', 'd', r.d));
            previewBody.appendChild(tr);
        });
    }

    // Picking a dropdown value, or typing in the description cell, writes straight
    // into the matching hidden field — per-operation (Operations[idx].X) or sheet-level.
    if (previewBody) {
        previewBody.addEventListener('input', syncFromPreview);
        previewBody.addEventListener('change', syncFromPreview);
    }
    function syncFromPreview(e) {
        var sel = e.target.closest('select.cell-select, input.cell-combo');
        if (sel) {
            var hidden;
            if (sel.dataset.sheetField) {
                hidden = document.querySelector('[name="' + sel.dataset.sheetField + '"]');
            } else {
                var prop = sel.dataset.prop === '__s__' ? 'CustomSetting' : sel.dataset.prop === '__t__' ? 'CustomTimeAllowed' : sel.dataset.prop;
                hidden = opsBody.querySelector('[name="Operations[' + sel.dataset.opIdx + '].' + prop + '"]');
            }
            if (hidden) hidden.value = sel.value;
            return;
        }
        var el = e.target.closest('[data-op-idx][contenteditable]');
        if (!el) return;
        var name = 'Operations[' + el.dataset.opIdx + '].' + fieldMap[el.dataset.field];
        var hidden = opsBody.querySelector('[name="' + name + '"]');
        if (!hidden) return;
        hidden.value = el.tagName === 'SELECT' ? el.value : el.textContent.trim();
    }

    function renumber() {
        opsBody.querySelectorAll('.op-row').forEach(function (row, i) {
            var h = row.querySelector('.op-handle');
            if (h) h.textContent = (i + 1);
        });
    }

    function applyMaterialDefault() {
        if (!material || material.value) return;       // don't override a chosen value
        var dn = (drawingNo ? drawingNo.value : '').toUpperCase();
        material.value = dn.indexOf('H') !== -1 ? 'S355JR / ST.52' : 'S235JR / ST.37';
    }

    // ----- wiring -----
    function wireRow(row) {
        var sel = row.querySelector('.op-key');
        sel.addEventListener('change', renderPreview);

        row.querySelector('.op-remove').addEventListener('click', function () {
            if (opsBody.querySelectorAll('.op-row').length > 1) {
                row.remove(); renumber(); renderPreview();
            }
        });
        row.querySelector('.op-up').addEventListener('click', function () {
            var prev = row.previousElementSibling;
            if (prev) { opsBody.insertBefore(row, prev); renumber(); renderPreview(); }
        });
        row.querySelector('.op-down').addEventListener('click', function () {
            var nxt = row.nextElementSibling;
            if (nxt) { opsBody.insertBefore(nxt, row); renumber(); renderPreview(); }
        });
    }

    opsBody.querySelectorAll('.op-row').forEach(wireRow);
    if (drawingNo && drawingNo.value) drawingNo.dataset.touched = '1';

    var counter = Date.now();
    addBtn.addEventListener('click', function () {
        var idx = 'n' + (counter++);
        var opts = defsList.map(function (d) {
            return '<option value="' + d.key + '">' + d.name + '</option>';
        }).join('');
        var html = tpl.innerHTML.split('__INDEX__').join(idx).split('__OPTIONS__').join(opts);
        var holder = document.createElement('div');
        holder.innerHTML = html.trim();
        var row = holder.firstElementChild;
        opsBody.appendChild(row);
        wireRow(row); renumber(); renderPreview();
        row.querySelector('.op-key').focus();
    });

    // header fields that affect the preview
    [drawingNo, project, section, cutLength, material].forEach(function (el) {
        if (el) el.addEventListener('input', renderPreview);
    });
    if (material) material.addEventListener('change', renderPreview);
    if (galv) galv.addEventListener('change', renderPreview);
    if (drawingNo) drawingNo.addEventListener('input', applyMaterialDefault);
    if (partNo && drawingNo) {
        partNo.addEventListener('input', function () {
            // convenience: keep Drawing No in sync until it is edited directly
            if (!drawingNo.dataset.touched) { drawingNo.value = partNo.value; applyMaterialDefault(); renderPreview(); }
        });
        drawingNo.addEventListener('input', function () { drawingNo.dataset.touched = '1'; });
    }

    // ----- auto-fill from drawing PDF -----
    var parseBtn = document.getElementById('parseDrawing');
    var fileInput = document.getElementById('drawingFile');
    var parseStatus = document.getElementById('parseStatus');

    function setStatus(msg, cls) {
        if (!parseStatus) return;
        parseStatus.textContent = msg;
        parseStatus.className = 'parse-status ' + (cls || '');
    }
    var pdfFallback = document.getElementById('pdfFallback');
    var pdfFallbackFrame = document.getElementById('pdfFallbackFrame');
    var pdfFallbackUrl = null;
    function showPdfFallback(file) {
        if (!pdfFallback || !pdfFallbackFrame || !file) return;
        if (pdfFallbackUrl) URL.revokeObjectURL(pdfFallbackUrl);
        pdfFallbackUrl = URL.createObjectURL(file);
        pdfFallbackFrame.src = pdfFallbackUrl;
        pdfFallback.classList.remove('hidden');
        pdfFallback.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    }
    function hidePdfFallback() {
        if (!pdfFallback) return;
        pdfFallback.classList.add('hidden');
        if (pdfFallbackUrl) { URL.revokeObjectURL(pdfFallbackUrl); pdfFallbackUrl = null; }
        pdfFallbackFrame.src = '';
    }
    function fillField(el, val) { if (el && val) el.value = val; }
    function setMaterial(val) {
        if (!material || !val) return;
        ensureOption(material, val);
    }

    if (parseBtn && fileInput) {
        parseBtn.addEventListener('click', function () {
            if (!fileInput.files || !fileInput.files[0]) { setStatus('Choose a PDF first.', 'err'); return; }
            var fd = new FormData();
            fd.append('file', fileInput.files[0]);
            setStatus('Reading drawing…', '');
            parseBtn.disabled = true;

            fetch(parseBtn.dataset.url, { method: 'POST', body: fd })
                .then(function (r) { return r.json(); })
                .then(function (d) {
                    parseBtn.disabled = false;
                    if (!d.ok) {
                        setStatus(d.error || 'Could not read drawing.', 'err');
                        showPdfFallback(fileInput.files[0]);
                        return;
                    }
                    hidePdfFallback();

                    fillField(partNo, d.partNo);
                    if (drawingNo) {
                        drawingNo.value = d.drawingNo || d.partNo || drawingNo.value;
                        drawingNo.dataset.touched = '1';
                    }
                    fillField(project, d.projectName);
                    if (d.section) { ensureOption(section, String(d.section).trim()); }
                    fillField(cutLength, d.cutLength);
                    var q = get('Qty');
                    if (q && d.qty != null && d.qty !== '') q.value = d.qty;
                    setMaterial(d.material);

                    renderPreview();
                    if (d.pageImage && d.crop) {
                        initCrop(d.pageImage, d.crop);
                        setStatus('Filled from drawing ✓  — review the crop below, then Save.', 'ok');
                    } else {
                        setStatus('Fields filled ✓ but the diagram preview could not render. '
                            + (d.previewError || '') + ' (The sheet will still save without the image.)', 'err');
                    }
                })
                .catch(function () { parseBtn.disabled = false; setStatus('Upload failed.', 'err'); });
        });
    }

    // ----- interactive diagram cropper -----
    var cropCard = document.getElementById('cropCard');
    var cropStage = document.getElementById('cropStage');
    var cropImage = document.getElementById('cropImage');
    var cropBox = document.getElementById('cropBox');
    var cropReset = document.getElementById('cropReset');
    var cropReadout = document.getElementById('cropReadout');
    var hX = document.getElementById('cropX'), hY = document.getElementById('cropY'),
        hW = document.getElementById('cropW'), hH = document.getElementById('cropH');
    var hData = document.getElementById('diagramData');

    var norm = { x: 0.05, y: 0.06, w: 0.9, h: 0.45 };
    var suggested = null;
    var MINN = 0.02;
    var drag = null;

    function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }
    function imgDims() { return { w: cropImage.clientWidth, h: cropImage.clientHeight }; }

    function writeNorm() {
        norm.x = clamp(norm.x, 0, 1 - MINN);
        norm.y = clamp(norm.y, 0, 1 - MINN);
        norm.w = clamp(norm.w, MINN, 1 - norm.x);
        norm.h = clamp(norm.h, MINN, 1 - norm.y);
        if (hX) hX.value = norm.x.toFixed(5);
        if (hY) hY.value = norm.y.toFixed(5);
        if (hW) hW.value = norm.w.toFixed(5);
        if (hH) hH.value = norm.h.toFixed(5);
        if (cropReadout) cropReadout.textContent =
            'crop ' + Math.round(norm.w * 100) + '% × ' + Math.round(norm.h * 100) + '% of page';
    }

    function layoutBox() {
        var d = imgDims();
        cropBox.style.left = (norm.x * d.w) + 'px';
        cropBox.style.top = (norm.y * d.h) + 'px';
        cropBox.style.width = (norm.w * d.w) + 'px';
        cropBox.style.height = (norm.h * d.h) + 'px';
    }

    function initCrop(dataUri, c) {
        suggested = { x: c.x, y: c.y, w: c.w, h: c.h };
        norm = { x: c.x, y: c.y, w: c.w, h: c.h };
        cropImage.onload = function () {
            cropCard.style.display = '';
            layoutBox();
            writeNorm();
        };
        cropImage.src = dataUri;
        if (cropImage.complete && cropImage.naturalWidth) {
            cropCard.style.display = '';
            layoutBox(); writeNorm();
        }
    }

    function onDown(e, mode) {
        e.preventDefault();
        var d = imgDims();
        drag = { mode: mode, mx: e.clientX, my: e.clientY,
                 x: norm.x, y: norm.y, w: norm.w, h: norm.h, dw: d.w, dh: d.h };
        document.addEventListener('pointermove', onMove);
        document.addEventListener('pointerup', onUp);
        document.addEventListener('pointercancel', onUp);
    }
    function onMove(e) {
        if (!drag) return;
        var dx = (e.clientX - drag.mx) / drag.dw;
        var dy = (e.clientY - drag.my) / drag.dh;
        if (drag.mode === 'move') {
            norm.x = clamp(drag.x + dx, 0, 1 - drag.w);
            norm.y = clamp(drag.y + dy, 0, 1 - drag.h);
        } else {
            var x = drag.x, y = drag.y, w = drag.w, h = drag.h;
            if (drag.mode.indexOf('l') !== -1) { x = clamp(drag.x + dx, 0, drag.x + drag.w - MINN); w = drag.w + (drag.x - x); }
            if (drag.mode.indexOf('r') !== -1) { w = clamp(drag.w + dx, MINN, 1 - drag.x); }
            if (drag.mode.indexOf('t') !== -1) { y = clamp(drag.y + dy, 0, drag.y + drag.h - MINN); h = drag.h + (drag.y - y); }
            if (drag.mode.indexOf('b') !== -1) { h = clamp(drag.h + dy, MINN, 1 - drag.y); }
            norm.x = x; norm.y = y; norm.w = w; norm.h = h;
        }
        layoutBox(); writeNorm();
    }
    function onUp() {
        drag = null;
        document.removeEventListener('pointermove', onMove);
        document.removeEventListener('pointerup', onUp);
        document.removeEventListener('pointercancel', onUp);
    }

    if (cropBox) {
        cropBox.addEventListener('pointerdown', function (e) {
            if (e.target.classList.contains('crop-h')) return;
            onDown(e, 'move');
        });
        ['tl', 'tr', 'bl', 'br'].forEach(function (cls) {
            var hnd = cropBox.querySelector('.crop-h.' + cls);
            if (hnd) hnd.addEventListener('pointerdown', function (e) { e.stopPropagation(); onDown(e, cls); });
        });
    }
    if (cropReset) {
        cropReset.addEventListener('click', function () {
            if (suggested) { norm = { x: suggested.x, y: suggested.y, w: suggested.w, h: suggested.h }; layoutBox(); writeNorm(); }
        });
    }
    window.addEventListener('resize', function () {
        if (cropCard && cropCard.style.display !== 'none') layoutBox();
    });

    // On submit: crop the preview image in-browser and post it as a PNG.
    var form = cropCard ? cropCard.closest('form') : null;
    if (form) {
        form.addEventListener('submit', function () {
            try {
                if (!hData || !cropImage || !cropImage.src || !cropImage.naturalWidth) return;
                var iw = cropImage.naturalWidth, ih = cropImage.naturalHeight;
                var sx = Math.round(norm.x * iw), sy = Math.round(norm.y * ih);
                var sw = Math.round(norm.w * iw), sh = Math.round(norm.h * ih);
                if (sw < 2 || sh < 2) return;
                var canvas = document.createElement('canvas');
                canvas.width = sw; canvas.height = sh;
                var ctx = canvas.getContext('2d');
                ctx.fillStyle = '#fff'; ctx.fillRect(0, 0, sw, sh);
                ctx.drawImage(cropImage, sx, sy, sw, sh, 0, 0, sw, sh);
                hData.value = canvas.toDataURL('image/png');
            } catch (err) { /* leave hData empty; server falls back */ }
        });
    }

    renderPreview();
})();
