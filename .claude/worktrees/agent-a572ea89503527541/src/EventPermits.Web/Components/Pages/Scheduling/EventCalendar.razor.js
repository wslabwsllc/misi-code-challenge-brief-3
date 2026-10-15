// Keyboard movement between calendar days (page 07). Roving tabindex: one day button is in the tab order; arrow keys,
// Home and End move focus inside the month or week grid. Enter and Space activate the focused day (native buttons).
export function init(host) {
  if (!host || host.dataset.calKeys === "1") return;
  host.dataset.calKeys = "1";
  host.addEventListener("keydown", (event) => {
    const current = event.target.closest("[data-cal-day]");
    if (!current) return;
    const grid = current.closest("[data-cols]");
    if (!grid) return;
    const days = Array.from(grid.querySelectorAll("[data-cal-day]"));
    const cols = Number(grid.dataset.cols || 7);
    const index = days.indexOf(current);
    const rowStart = index - (index % cols);
    const moves = {
      ArrowLeft: index - 1,
      ArrowRight: index + 1,
      ArrowUp: index - cols,
      ArrowDown: index + cols,
      Home: rowStart,
      End: Math.min(rowStart + cols - 1, days.length - 1),
    };
    if (!(event.key in moves)) return;
    const target = moves[event.key];
    if (target < 0 || target >= days.length) return;
    event.preventDefault();
    days.forEach((d) => { d.tabIndex = -1; });
    days[target].tabIndex = 0;
    days[target].focus();
  });
}
