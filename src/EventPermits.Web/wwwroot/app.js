// Small helpers for interactive pages. No third-party scripts (CSP script-src 'self').
window.eventPermits = {
  focusFirstInvalid() {
    const el = document.querySelector('[aria-invalid="true"]');
    if (el) { el.focus(); return true; }
    return false;
  },
  focus(selector) {
    const el = document.querySelector(selector);
    if (el) el.focus();
  },
  print() { window.print(); },
  open(url) { window.location.assign(url); },
  copy(text) { return navigator.clipboard ? navigator.clipboard.writeText(text) : Promise.resolve(); }
};
