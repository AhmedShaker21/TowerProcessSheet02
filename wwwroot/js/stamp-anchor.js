// Repositions any stamp/signature that was anchored to a word (instead of a page
// corner) so it lands next to that exact text on every sheet — robust even when
// sheets differ in length, since the position comes from where the text actually
// renders, not from a percentage of the total page height.
(function () {
    function findTextRect(root, phrase) {
        var target = (phrase || '').trim().toLowerCase();
        if (!target) return null;
        var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null);
        var node;
        while ((node = walker.nextNode())) {
            var text = node.textContent || '';
            var idx = text.toLowerCase().indexOf(target);
            if (idx === -1) continue;
            try {
                var range = document.createRange();
                range.setStart(node, idx);
                range.setEnd(node, Math.min(idx + target.length, text.length));
                var rect = range.getBoundingClientRect();
                if (rect && (rect.width || rect.height)) return rect;
            } catch (e) { /* try the next matching node */ }
        }
        return null;
    }

    function positionStamps() {
        document.querySelectorAll('.route-card-wrapper').forEach(function (wrapper) {
            var wrapRect = wrapper.getBoundingClientRect();
            wrapper.querySelectorAll('.placed-stamp[data-anchor-text]').forEach(function (img) {
                var phrase = img.dataset.anchorText;
                if (!phrase) return;
                var rect = findTextRect(wrapper, phrase);
                if (!rect) return;   // text not found on this sheet — keep the corner fallback position
                var dx = parseFloat(img.dataset.offsetX) || 0;
                var dy = parseFloat(img.dataset.offsetY) || 0;
                img.style.left = (rect.right - wrapRect.left + dx) + 'px';
                img.style.top = (rect.top - wrapRect.top + dy) + 'px';
                img.style.right = 'auto';
                img.style.bottom = 'auto';
            });
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', positionStamps);
    } else {
        positionStamps();
    }
    window.addEventListener('beforeprint', positionStamps);
    window.addEventListener('resize', positionStamps);
    window.stampAnchorReposition = positionStamps;  // callable again after live edits (e.g. Place Stamps page)
})();
