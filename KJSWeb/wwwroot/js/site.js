document.addEventListener('DOMContentLoaded', function () {
    // Mobile hamburger toggle
    const toggle = document.getElementById('mobileToggle');
    const mobileNav = document.getElementById('mobileNav');

    if (toggle && mobileNav) {
        toggle.addEventListener('click', function () {
            toggle.classList.toggle('active');
            mobileNav.classList.toggle('active');
        });
    }

    // Mobile categories dropdown
    const catToggle = document.getElementById('mobileCatToggle');
    const catDropdown = document.getElementById('mobileCatDropdown');
    const catArrow = document.getElementById('mobileCatArrow');

    if (catToggle && catDropdown) {
        catToggle.addEventListener('click', function () {
            catDropdown.classList.toggle('active');
            if (catArrow) {
                catArrow.textContent = catDropdown.classList.contains('active') ? '▲' : '▼';
            }
        });
    }
});
