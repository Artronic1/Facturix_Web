// ──────────────────────────────────────────────────────
// Facturix Web — Client-Side Utilities
// ──────────────────────────────────────────────────────

(function () {
    'use strict';

    // ─── Auto-dismiss flash messages after 5 seconds ───
    document.querySelectorAll('.alert-dismissible').forEach(function (alert) {
        setTimeout(function () {
            var bsAlert = bootstrap.Alert.getOrCreateInstance(alert);
            if (bsAlert) {
                alert.style.transition = 'opacity 0.4s ease, transform 0.4s ease';
                alert.style.opacity = '0';
                alert.style.transform = 'translateY(-10px)';
                setTimeout(function () { bsAlert.close(); }, 400);
            }
        }, 5000);
    });

    // ─── Auto-focus first visible search input on the page ───
    var searchInput = document.querySelector('input[name="search"], input[name="filter"]');
    if (searchInput && searchInput.offsetParent !== null) {
        // Only focus if the field is empty (don't steal focus on filtered results)
        if (!searchInput.value) {
            searchInput.focus();
        }
    }

    // ─── Confirm dialogs with better UX ───
    document.querySelectorAll('form[data-confirm]').forEach(function (form) {
        form.addEventListener('submit', function (e) {
            if (!confirm(form.dataset.confirm)) {
                e.preventDefault();
            }
        });
    });

    // ─── Prevent double-submit on forms ───
    document.querySelectorAll('form[method="post"]').forEach(function (form) {
        form.addEventListener('submit', function () {
            var buttons = form.querySelectorAll('button[type="submit"], button:not([type])');
            buttons.forEach(function (btn) {
                // Small delay so the form submits before disabling
                setTimeout(function () {
                    btn.disabled = true;
                    btn.style.opacity = '0.65';
                }, 50);
            });
        });
    });

    // ─── Tooltips init (if any) ───
    var tooltipTriggerList = [].slice.call(document.querySelectorAll('[data-bs-toggle="tooltip"]'));
    tooltipTriggerList.forEach(function (tooltipTriggerEl) {
        new bootstrap.Tooltip(tooltipTriggerEl);
    });

    // ─── Seamless AJAX Forms (No Reload) ───
    function bindAjaxForms() {
        document.querySelectorAll('form[data-ajax="true"]').forEach(function (form) {
            // Prevent binding multiple times
            if (form.dataset.ajaxBound) return;
            form.dataset.ajaxBound = "true";

            form.addEventListener('submit', async function (e) {
                e.preventDefault();
                var btn = form.querySelector('button[type="submit"], button:not([type])');
                if (btn) { btn.disabled = true; btn.style.opacity = '0.65'; }

                var scrollY = window.scrollY; // Save scroll position

                try {
                    var response = await fetch(form.action, {
                        method: form.method || 'POST',
                        body: new FormData(form),
                        headers: { 'X-Requested-With': 'XMLHttpRequest' }
                    });

                    if (response.ok || response.redirected) {
                        var html = await response.text();
                        var parser = new DOMParser();
                        var doc = parser.parseFromString(html, 'text/html');
                        
                        var newMain = doc.querySelector('main');
                        var currentMain = document.querySelector('main');
                        
                        if (newMain && currentMain) {
                            currentMain.innerHTML = newMain.innerHTML;
                            
                            // Replace flash messages
                            var newFlash = doc.querySelector('#flash-messages-container');
                            var currentFlash = document.querySelector('#flash-messages-container');
                            if (newFlash && currentFlash) {
                                currentFlash.innerHTML = newFlash.innerHTML;
                            }

                            // Restore scroll
                            window.scrollTo(0, scrollY);

                            // Re-bind events to new DOM elements
                            bindAjaxForms();
                        } else {
                            window.location.reload();
                        }
                    } else {
                        window.location.reload();
                    }
                } catch (err) {
                    window.location.reload();
                }
            });
        });
    }

    // Initialize seamless forms
    bindAjaxForms();

})();
