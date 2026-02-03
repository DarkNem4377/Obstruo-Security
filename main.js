document.addEventListener("DOMContentLoaded", function () {
  /* =========================================
     1. MOBILE MENU (hamburger ↔ overlay)
     ========================================= */
  const mobileToggle = document.querySelector(".mobile-menu-toggle");
  const mobileMenu   = document.getElementById("mobileMenu");

  if (mobileToggle && mobileMenu) {
    // Close menu when a nav link inside it is clicked
    mobileMenu.querySelectorAll("a").forEach(function (link) {
      link.addEventListener("click", function () {
        mobileMenu.classList.remove("active");
        mobileToggle.classList.remove("active");
        mobileToggle.setAttribute("aria-expanded", "false");
      });
    });

    mobileToggle.addEventListener("click", function () {
      var isOpen = mobileMenu.classList.contains("active");

      mobileMenu.classList.toggle("active");
      mobileToggle.classList.toggle("active");
      mobileToggle.setAttribute("aria-expanded", String(!isOpen));
    });
  }

  /* =========================================
     2. ACCORDION / EXPANDABLE LOGIC
     ========================================= */
  const expandTriggers = document.querySelectorAll(
    ".expand-button, .accordion-header"
  );

  expandTriggers.forEach(function (trigger) {
    trigger.addEventListener("click", function () {
      var content      = this.nextElementSibling;
      var isActive     = this.classList.contains("active");
      var innerContent = content.querySelector("div");

      if (isActive) {
        content.style.height = "0";
        content.classList.remove("expanded");
        this.classList.remove("active");
        this.setAttribute("aria-expanded", "false");
      } else {
        this.classList.add("active");
        this.setAttribute("aria-expanded", "true");
        content.classList.add("expanded");
        content.style.height = innerContent.scrollHeight + "px";
      }
    });
  });

  /* =========================================
     3. MANUAL TABLE OF CONTENTS (TOC)
     ========================================= */
  const tocLinks = document.querySelectorAll(".toc-link");
  if (tocLinks.length > 0) {
    tocLinks.forEach(function (link) {
      link.addEventListener("click", function (e) {
        e.preventDefault();
        var targetId      = this.getAttribute("href").substring(1);
        var targetElement = document.getElementById(targetId);

        if (targetElement) {
          var trigger = targetElement.previousElementSibling;
          if (!trigger.classList.contains("active")) {
            trigger.click();
          }
          setTimeout(function () {
            trigger.scrollIntoView({ behavior: "smooth", block: "center" });
          }, 100);
        }
      });
    });

    window.addEventListener("scroll", function () {
      var scrollPos  = window.scrollY + 200;
      var currentId  = "";

      document.querySelectorAll(".accordion-item").forEach(function (item) {
        if (item.offsetTop <= scrollPos) {
          var content = item.querySelector(".accordion-content");
          if (content) currentId = content.id;
        }
      });

      tocLinks.forEach(function (link) {
        link.classList.remove("active");
        if (link.getAttribute("href") === "#" + currentId) {
          link.classList.add("active");
        }
      });
    });
  }

  /* =========================================
     4. BACK TO TOP BUTTON
     ========================================= */
  const backToTopBtn = document.querySelector(".back-to-top");
  if (backToTopBtn) {
    window.addEventListener("scroll", function () {
      if (window.scrollY > 300) {
        backToTopBtn.classList.add("visible");
      } else {
        backToTopBtn.classList.remove("visible");
      }
    });

    backToTopBtn.addEventListener("click", function () {
      window.scrollTo({ top: 0, behavior: "smooth" });
    });
  }

  /* =========================================
     5. GENERAL ANIMATIONS (Fade In)
     ========================================= */
  const animatedElements = document.querySelectorAll(
    ".feature-card, .policy-card"
  );
  const observer = new IntersectionObserver(
    function (entries) {
      entries.forEach(function (entry) {
        if (entry.isIntersecting) {
          entry.target.style.opacity    = "1";
          entry.target.style.transform  = "translateY(0)";
          observer.unobserve(entry.target);
        }
      });
    },
    { threshold: 0.1 }
  );

  animatedElements.forEach(function (el) {
    el.style.opacity    = "0";
    el.style.transform  = "translateY(20px)";
    el.style.transition = "opacity 0.6s ease, transform 0.6s ease";
    observer.observe(el);
  });
});
