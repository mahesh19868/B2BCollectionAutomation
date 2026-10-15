// Small progressive enhancements. Every rule is also enforced on the server.
(function () {
    'use strict';

    // Confirmation for buttons with data-confirm.
    document.addEventListener('click', function (event) {
        var button = event.target.closest('[data-confirm]');
        if (button && !window.confirm(button.getAttribute('data-confirm'))) {
            event.preventDefault();
        }
    });

    // Prevent accidental double submission of POST forms.
    document.addEventListener('submit', function (event) {
        var form = event.target;
        if (event.defaultPrevented || (form.method || '').toLowerCase() !== 'post') {
            return;
        }
        if (form.dataset.submitted === 'true') {
            event.preventDefault();
            return;
        }
        form.dataset.submitted = 'true';
        form.querySelectorAll('button[type="submit"]').forEach(function (b) { b.disabled = true; });
    });

    // Payment allocation helper.
    var form = document.querySelector('[data-allocation-form]');
    if (!form) {
        return;
    }

    var amountInput = form.querySelector('[data-payment-amount]');
    var inputs = Array.prototype.slice.call(form.querySelectorAll('[data-allocation]'));
    var allocatedLabel = form.querySelector('[data-allocated-total]');
    var unallocatedLabel = form.querySelector('[data-unallocated-total]');
    var formatter = new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', minimumFractionDigits: 2 });

    // Work in paise to avoid floating point rounding.
    function toPaise(value) {
        var n = parseFloat(value);
        return isNaN(n) ? 0 : Math.round(n * 100);
    }

    function refresh() {
        var allocated = inputs.reduce(function (sum, i) { return sum + toPaise(i.value); }, 0);
        var payment = toPaise(amountInput.value);
        if (allocatedLabel) { allocatedLabel.textContent = formatter.format(allocated / 100); }
        if (unallocatedLabel) { unallocatedLabel.textContent = formatter.format((payment - allocated) / 100); }
    }

    var autoButton = form.querySelector('[data-auto-allocate]');
    if (autoButton) {
        autoButton.addEventListener('click', function () {
            var remaining = toPaise(amountInput.value);
            inputs.forEach(function (input) {
                var outstanding = toPaise(input.getAttribute('data-outstanding'));
                var take = Math.max(0, Math.min(outstanding, remaining));
                input.value = take > 0 ? (take / 100).toFixed(2) : '';
                remaining -= take;
            });
            refresh();
        });
    }

    form.addEventListener('input', refresh);
    refresh();
})();
