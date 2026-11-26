document.addEventListener("DOMContentLoaded", function () {
  var currentPath = window.location.pathname.toLowerCase();
  document.querySelectorAll(".app-nav a.nav-link").forEach(function (link) {
    var href = link.getAttribute("href");
    if (href && href !== "/" && currentPath.startsWith(href.toLowerCase())) {
      link.classList.add("active");
      link.setAttribute("aria-current", "page");
    }
  });

  document.querySelectorAll("form[data-submit-feedback]").forEach(function (form) {
    form.addEventListener("submit", function () {
      var button = form.querySelector("[type='submit']");
      if (!button || button.disabled) return;
      button.disabled = true;
      button.innerHTML = '<span class="spinner-border submit-spinner me-2" aria-hidden="true"></span>' +
        (button.getAttribute("data-loading-text") || "Please wait...");
    });
  });

  document.querySelectorAll("[data-warranty-status]").forEach(function (select) {
    var form = select.form;
    var fields = form && form.querySelector("[data-warranty-fields]");
    if (!fields) return;

    function updateWarrantyFields() {
      var isConfirmed = select.value === select.getAttribute("data-confirmed-value");
      fields.classList.toggle("d-none", !isConfirmed);
      fields.querySelectorAll("input, select, textarea").forEach(function (input) {
        input.disabled = !isConfirmed;
      });
    }

    select.addEventListener("change", updateWarrantyFields);
    updateWarrantyFields();
  });

  document.querySelectorAll("[data-coverage-category]").forEach(function (select) {
    var form = select.form;
    var invoiceFields = form && form.querySelector("[data-extended-invoice-fields]");
    if (!invoiceFields) return;

    function updateInvoiceFields() {
      var isExtended = select.value === select.getAttribute("data-extended-value");
      invoiceFields.classList.toggle("d-none", !isExtended);
      invoiceFields.querySelectorAll("input").forEach(function (input) {
        input.disabled = !isExtended;
        input.required = isExtended;
      });
    }

    select.addEventListener("change", updateInvoiceFields);
    updateInvoiceFields();
  });

  document.querySelectorAll("table th").forEach(function (header) {
    if (!header.getAttribute("scope")) header.setAttribute("scope", "col");
  });
});
