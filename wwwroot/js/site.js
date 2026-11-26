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

  document.querySelectorAll("table th").forEach(function (header) {
    if (!header.getAttribute("scope")) header.setAttribute("scope", "col");
  });
});
