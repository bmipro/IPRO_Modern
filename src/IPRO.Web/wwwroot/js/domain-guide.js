// 595: My Website's "Connect your domain" guide (Views/Website/_DomainGuide.cshtml).
//
// Each open guide asks /portal/Website/DomainGuide/{id} what DNS shows about every 20 seconds while the page is in
// front and the guide is in view, and ticks each step as its record is seen. The server keeps its own rate limit; a
// refusal (429) or an error only slows the page down, quietly. One step is open at a time: the current one, or the one
// the adviser opened. When a step is done and the adviser is working inside the guide, the keyboard focus moves on to
// the next step, and a polite live region says so. Everything the server sends is set as text (textContent): some of it
// is what a domain's own DNS holds.
//
// 603 (owner, 2026-10-24: "I did not realize that I was already on my second step"): what the guide does shows too.
// An hourglass turns while iPro looks and the time to the next look counts down; when a step changes on its own a line
// says so ("Step 1 done. Now step 2: ...") until the next change, the new step glows once, and the bar of the four steps
// moves on. The browser tab's title carries the step of the domain being set up, and that guide keeps looking once a
// minute while the adviser is on the registrar's site in another tab, so the tab says "Live" when it is.
(function () {
  'use strict';
  if (window.iproDomainGuide) return;
  window.iproDomainGuide = true;

  var TICK = String.fromCharCode(0x2713);
  var INTERVAL = 20000;          // about every 20 seconds
  var SLOW_INTERVAL = 60000;     // after half an hour with the page open
  var SLOW_AFTER = 30 * 60 * 1000;
  var MAX_INTERVAL = 5 * 60 * 1000;
  var HIDDEN_INTERVAL = 60000;   // 603: the guide in the tab's title, while the adviser is in another tab
  var SIGNED_OUT = 'Your session has ended. Reload the page to sign in again.';
  var COULD_NOT = 'We could not check just now; we will try again in a moment.';
  var CHECKING = 'Checking your domain now...';
  var BASE_TITLE = document.title;

  function storage(name) {
    try { return window.sessionStorage.getItem(name); } catch (e) { return null; }
  }
  function store(name, value) {
    try { window.sessionStorage.setItem(name, value); } catch (e) { /* private window: forget it */ }
  }

  function text(element, value) {
    if (element) element.textContent = value == null ? '' : String(value);
  }
  function show(element, visible) {
    if (element) element.hidden = !visible;
  }
  function stateClass(state) {
    return String(state || 'upcoming').toLowerCase();
  }
  function each(root, selector, action) {
    Array.prototype.forEach.call(root.querySelectorAll(selector), action);
  }

  function Guide(section) {
    this.section = section;
    this.id = section.getAttribute('data-domain-guide');
    this.url = section.getAttribute('data-guide-url');
    this.current = parseInt(section.getAttribute('data-guide-current') || '0', 10);
    this.live = section.getAttribute('data-guide-live') === 'true';
    this.refused = section.getAttribute('data-guide-refused') === 'true';
    this.shortPending = !!section.querySelector('[data-guide-step="3"].guide-step--current, [data-guide-step="3"].guide-step--upcoming');
    this.interval = INTERVAL;
    this.started = Date.now();
    this.timer = null;
    this.busy = false;
    this.stopped = false;
    this.inView = true;
    this.announcer = section.querySelector('[data-guide-announce]');
    this.activity = section.querySelector('[data-guide-activity]');
    this.nextAt = 0;
    this.ticker = null;
    this.titled = false;
    this.arrivedTimer = null;
    this.bind();
  }

  Guide.prototype.token = function () {
    var input = this.section.querySelector('input[name="__RequestVerificationToken"]');
    return input ? input.value : '';
  };

  Guide.prototype.steps = function () {
    return Array.prototype.slice.call(this.section.querySelectorAll('[data-guide-step]'));
  };

  Guide.prototype.step = function (number) {
    return this.section.querySelector('[data-guide-step="' + number + '"]');
  };

  Guide.prototype.skipped = function () {
    return storage('domain-guide-skip-' + this.id) === '1';
  };

  // Opens one step and closes the others (an accordion: one open at a time).
  Guide.prototype.open = function (number, moveFocus) {
    this.steps().forEach(function (step) {
      var isIt = step.getAttribute('data-guide-step') === String(number);
      var toggle = step.querySelector('.guide-step__toggle');
      var body = step.querySelector('.guide-step__body');
      if (toggle) toggle.setAttribute('aria-expanded', isIt ? 'true' : 'false');
      show(body, isIt);
      if (isIt && moveFocus && toggle) {
        toggle.focus();
        if (toggle.scrollIntoView) toggle.scrollIntoView({ block: 'nearest' });
      }
    });
  };

  // The step the adviser is on, for screen readers: the server's current step, or step 4 once step 3 was skipped.
  Guide.prototype.markCurrent = function (number) {
    this.steps().forEach(function (step) {
      if (step.getAttribute('data-guide-step') === String(number)) step.setAttribute('aria-current', 'step'); else step.removeAttribute('aria-current');
    });
  };

  Guide.prototype.announce = function (message) {
    if (!this.announcer) return;
    // Emptied first, so the same words said twice are said twice.
    this.announcer.textContent = '';
    var announcer = this.announcer;
    window.setTimeout(function () { announcer.textContent = message; }, 50);
  };

  // 603: the line under the heading. busy: the hourglass turns while iPro looks; waiting: it stands, and the time to
  // the next look counts down; done: a tick, nothing left to watch. Not a live region: a countdown would talk every
  // second (the announcer says what matters).
  Guide.prototype.setActivity = function (state) {
    var activity = this.activity;
    if (!activity) return;
    activity.className = 'guide-activity guide-activity--' + state;
    var icon = activity.querySelector('[data-guide-activity-icon]');
    if (icon) icon.className = 'fas ' + (state === 'done' ? 'fa-circle-check' : state === 'stopped' ? 'fa-circle-exclamation' : 'fa-hourglass-half') + ' guide-activity__icon';
    var check = this.section.querySelector('[data-guide-check]');
    if (check) {
      check.classList.toggle('is-checking', state === 'busy');
      text(check, state === 'busy' ? 'Checking...' : 'Check now');
    }
    this.tick();
  };

  Guide.prototype.tick = function () {
    var next = this.activity ? this.activity.querySelector('[data-guide-next]') : null;
    if (!next) return;
    if (this.busy || !this.nextAt || !this.watching() || this.section.hidden) { text(next, ''); return; }
    var seconds = Math.ceil((this.nextAt - Date.now()) / 1000);
    text(next, seconds > 0 ? 'Next check in ' + seconds + (seconds === 1 ? ' second.' : ' seconds.') : 'Checking again in a moment.');
  };

  // The countdown's clock: one per guide, only while there is something to count down to.
  Guide.prototype.startTicker = function () {
    var guide = this;
    if (this.ticker !== null) return;
    this.ticker = window.setInterval(function () {
      if (!guide.nextAt || !guide.watching()) { window.clearInterval(guide.ticker); guide.ticker = null; guide.tick(); return; }
      if (document.visibilityState === 'visible') guide.tick();
    }, 1000);
  };

  // 603: says on the page what the announcer says aloud, until the next change. back: a step that was done is not seen
  // any more (a record removed), so the line is amber, not green.
  Guide.prototype.moved = function (message, back) {
    var line = this.section.querySelector('[data-guide-moved]');
    if (!line) return;
    line.className = 'guide-moved' + (back ? ' guide-moved--back' : '');
    var icon = line.querySelector('[data-guide-moved-icon]');
    if (icon) icon.className = 'fas ' + (back ? 'fa-circle-exclamation' : 'fa-circle-check') + ' guide-moved__icon';
    text(line.querySelector('[data-guide-moved-text]'), message);
    show(line, true);
  };

  // The step just reached glows twice (not with reduced motion), and comes into view if it is off the screen.
  Guide.prototype.arrive = function (number, focused) {
    var guide = this;
    var step = this.step(number);
    if (!step) return;
    window.clearTimeout(this.arrivedTimer);
    each(this.section, '.guide-step--arrived', function (element) { element.classList.remove('guide-step--arrived'); });
    step.classList.add('guide-step--arrived');
    this.arrivedTimer = window.setTimeout(function () { step.classList.remove('guide-step--arrived'); guide.arrivedTimer = null; }, 2600);
    if (focused) return;   // open() has already brought it into view with the focus
    var box = step.getBoundingClientRect();
    if ((box.top < 0 || box.bottom > window.innerHeight) && step.scrollIntoView) {
      var still = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
      step.scrollIntoView({ block: 'nearest', behavior: still ? 'auto' : 'smooth' });
    }
  };

  // The bar of the four steps under the heading: each part as its step, the shown one (step 4 once 3 is skipped) current.
  Guide.prototype.paintMeter = function (shown) {
    var guide = this;
    each(this.section, '[data-guide-meter]', function (part) {
      var number = part.getAttribute('data-guide-meter');
      var step = guide.step(number);
      var state = 'upcoming';
      if (step) ['done', 'current', 'notneeded', 'upcoming'].forEach(function (name) { if (step.classList.contains('guide-step--' + name)) state = name; });
      if (String(shown) === number) state = 'current';
      else if (state === 'current') state = 'notneeded';
      part.className = 'guide-meter__part guide-meter__part--' + state;
    });
  };

  // The tab's title: the step of the domain being set up, so it shows from another tab.
  Guide.prototype.setTitle = function (shown, live) {
    if (!this.titled) return;
    if (this.section.hidden) { document.title = BASE_TITLE; return; }
    var count = this.steps().length;
    document.title = (live ? 'Live' : 'Step ' + shown + ' of ' + count) + (BASE_TITLE ? ' | ' + BASE_TITLE : '');
  };

  // The guide in the tab's title looks once a minute while the adviser is in another tab.
  Guide.prototype.background = function () {
    return this.titled && !this.section.hidden && this.inView && document.visibilityState !== 'visible';
  };

  Guide.prototype.bind = function () {
    var guide = this;
    this.steps().forEach(function (step) {
      var toggle = step.querySelector('.guide-step__toggle');
      if (!toggle) return;
      toggle.addEventListener('click', function () {
        var expanded = toggle.getAttribute('aria-expanded') === 'true';
        if (expanded) {
          toggle.setAttribute('aria-expanded', 'false');
          show(step.querySelector('.guide-step__body'), false);
        } else {
          guide.open(step.getAttribute('data-guide-step'), false);
        }
      });
    });

    each(this.section, '[data-copy]', function (button) {
      button.addEventListener('click', function () { guide.copy(button); });
    });

    var skip = this.section.querySelector('[data-guide-skip]');
    if (skip) {
      skip.addEventListener('click', function () {
        store('domain-guide-skip-' + guide.id, '1');
        guide.markCurrent(4);
        guide.open(4, true);
        guide.announce('Step 3 skipped. Step 4: iPro finishes.');
        guide.moved('Step 3 skipped. Now step 4: iPro finishes.', false);
        guide.paintMeter(4);
        guide.arrive(4, true);
        guide.setTitle(4, guide.live);
      });
    }

    // Check now: never disabled (that would drop the keyboard focus); a click while a look is on its way does nothing.
    var check = this.section.querySelector('[data-guide-check]');
    if (check) {
      check.addEventListener('click', function () {
        if (check.getAttribute('aria-disabled') === 'true') return;
        guide.stopped = false;
        guide.poll(true);
      });
    }
  };

  // Copies the record's text. The browser can refuse the clipboard (a page in a frame, an older browser): then the text
  // is selected, and the button says what to press.
  Guide.prototype.copy = function (button) {
    var guide = this;
    var target = document.getElementById(button.getAttribute('data-copy'));
    if (!target) return;
    var value = target.textContent.trim();
    if (!value) return;
    var label = button.getAttribute('aria-label') || 'Copy';
    var touch = window.matchMedia && window.matchMedia('(pointer: coarse)').matches;
    var reset = function () {
      window.setTimeout(function () { button.textContent = 'Copy'; button.setAttribute('aria-label', label); }, 2500);
    };
    var selectIt = function () {
      var range = document.createRange();
      range.selectNodeContents(target);
      var selection = window.getSelection();
      selection.removeAllRanges();
      selection.addRange(range);
      button.textContent = touch ? 'Selected' : 'Press Ctrl+C';
      button.setAttribute('aria-label', touch ? 'Selected. Use your phone\'s Copy' : 'Selected. Press Ctrl+C to copy');
      guide.announce('Selected ' + value + (touch ? '. Use your phone\'s Copy.' : '. Press Ctrl+C (Command+C on a Mac) to copy it.'));
      reset();
    };
    if (navigator.clipboard && window.isSecureContext) {
      navigator.clipboard.writeText(value).then(function () {
        button.textContent = 'Copied';
        button.setAttribute('aria-label', 'Copied');
        guide.announce('Copied ' + value);
        reset();
      }, selectIt);
    } else {
      selectIt();
    }
  };

  Guide.prototype.visible = function () {
    return !this.section.hidden && this.inView && document.visibilityState === 'visible';
  };

  // Whether there is anything left to watch: a domain not live yet, or a live one whose short address is still to do.
  Guide.prototype.watching = function () {
    return !this.refused && !this.stopped && (!this.live || this.shortPending);
  };

  Guide.prototype.schedule = function (delay) {
    var guide = this;
    window.clearTimeout(this.timer);
    this.timer = null;
    this.nextAt = 0;
    if (!this.watching()) { this.tick(); return; }
    // In another tab only the guide in the title looks, and once a minute at most (603).
    var wait = document.visibilityState === 'visible' ? delay : Math.max(delay, HIDDEN_INTERVAL);
    this.nextAt = Date.now() + wait;
    this.timer = window.setTimeout(function () { guide.timer = null; guide.poll(false); }, wait);
    this.tick();
    this.startTicker();
  };

  // once: a single look whatever the guide shows (a live domain's email, below); the timer then carries on as usual.
  Guide.prototype.poll = function (asked, once) {
    var guide = this;
    if (this.busy || this.refused) return;
    if (!asked && !once && (!(this.visible() || this.background()) || !this.watching())) { this.schedule(this.interval); return; }
    this.busy = true;
    var checked = this.section.querySelector('[data-guide-checked]');
    text(checked, CHECKING);
    this.setActivity('busy');
    var button = this.section.querySelector('[data-guide-check]');
    if (button) button.setAttribute('aria-disabled', 'true');

    // Check now (asked) lets the server run the full check at once, 15 seconds apart at most; the timer only looks.
    fetch(this.url + (asked ? '?asked=true' : ''), {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'RequestVerificationToken': this.token(), 'Accept': 'application/json' }
    }).then(function (response) {
      var json = (response.headers.get('Content-Type') || '').indexOf('application/json') >= 0;
      if (response.status === 429) {
        // Over the server's limit: wait longer, and say nothing alarming.
        guide.interval = Math.min(guide.interval * 2, MAX_INTERVAL);
        text(checked, 'We check again in a moment.');
        return null;
      }
      if (response.status === 404) {
        guide.stopped = true;
        text(checked, 'This domain is no longer on your account. Reload the page.');
        return null;
      }
      // Sent to the sign-in page, or the page's token no longer accepted: the session has ended.
      if (response.redirected || response.status === 400 || response.status === 401 || response.status === 403 || (response.ok && !json)) {
        guide.stopped = true;
        text(checked, SIGNED_OUT);
        return null;
      }
      if (!response.ok) {
        guide.interval = Math.min(guide.interval * 2, MAX_INTERVAL);
        text(checked, COULD_NOT);
        return null;
      }
      return response.json();
    }).then(function (data) {
      if (data) {
        guide.interval = Date.now() - guide.started > SLOW_AFTER ? SLOW_INTERVAL : INTERVAL;
        var announced = guide.update(data);
        if (asked) {
          // Check now within seconds of another check only looks again; say so rather than seem to do nothing.
          var words = data.checkedNow ? 'Checked just now.' : 'We checked a few seconds ago; this is still the latest.';
          text(checked, words);
          if (!announced) guide.announce(words);
        }
      }
    }).catch(function () {
      guide.interval = Math.min(guide.interval * 2, MAX_INTERVAL);
      text(checked, COULD_NOT);
    }).then(function () {
      guide.busy = false;
      if (button) button.removeAttribute('aria-disabled');
      guide.schedule(guide.interval);
      guide.setActivity(guide.stopped ? 'stopped' : guide.watching() ? 'waiting' : 'done');
    });
  };

  Guide.prototype.update = function (data) {
    var guide = this;
    var section = this.section;
    var host = data.host || {};
    var wholeNames = host.key === 'unknown' || host.key === 'generic';

    text(section.querySelector('[data-guide-heading]'), data.heading);
    // The row's line under the domain: the step it is on (once live, the page says so when reloaded).
    var summary = document.querySelector('[data-guide-summary="' + this.id + '"] [data-guide-summary-text]');
    if (!data.refusal && !data.live) text(summary, data.heading);
    // 603: gone live while the page was open: the row says so at once, as it will once the page is reloaded.
    if (!data.refusal && data.live && !this.live) {
      text(summary, 'Live and secured');
      var badge = document.querySelector('[data-guide-summary="' + this.id + '"] .badge');
      if (badge) { badge.className = 'badge bg-success'; text(badge, 'Live'); }
    }
    var problem = section.querySelector('[data-guide-problem]');
    if (problem) {
      text(problem.querySelector('[data-guide-problem-text]'), data.problem);
      show(problem, !!data.problem);
    }

    show(section.querySelector('[data-guide-host-line]'), !!host.name);
    text(section.querySelector('[data-guide-host-name]'), host.name);
    each(section, '[data-guide-host-where]', function (element) { text(element, host.where); });
    each(section, '[data-guide-host-namenote]', function (element) { text(element, host.nameNote); });
    each(section, '[data-guide-host-help]', function (link) {
      // Only a host's own https page, as the server lists it.
      var ok = typeof host.helpUrl === 'string' && host.helpUrl.indexOf('https://') === 0;
      if (ok) link.setAttribute('href', host.helpUrl); else link.removeAttribute('href');
      show(link, ok);
      text(link.querySelector('[data-guide-host-help-name]'), host.name);
    });

    var skipped = this.skipped();
    var current = data.currentStep || 0;
    var shown = current === 3 && skipped ? 4 : current;
    (data.steps || []).forEach(function (step) {
      var element = guide.step(step.number);
      if (!element) return;
      var state = stateClass(step.state);
      element.className = 'guide-step guide-step--' + state;
      text(element.querySelector('[data-guide-icon]'), state === 'done' ? TICK : String(step.number));
      text(element.querySelector('[data-guide-status]'), step.status);

      var seen = element.querySelector('[data-guide-seen]');
      if (seen) {
        seen.className = 'guide-seen guide-seen--' + (step.seenRight === true ? 'right' : step.seenRight === false ? 'wrong' : 'unknown');
        text(seen.querySelector('[data-guide-seen-text]'), step.seen);
        show(seen, !!step.seen);
      }
      var tip = element.querySelector('[data-guide-tip]');
      if (tip) {
        text(tip.querySelector('[data-guide-tip-host]'), host.name || 'your DNS host');
        text(tip.querySelector('[data-guide-tip-text]'), step.tip);
        show(tip, !!step.tip);
      }
      (step.records || []).forEach(function (record) {
        text(element.querySelector('[data-guide-name="' + record.key + '"]'), record.name);
        show(element.querySelector('[data-guide-name-empty="' + record.key + '"]'), !record.name);
        show(element.querySelector('[data-guide-copy-name="' + record.key + '"]'), !!record.name);
        show(element.querySelector('[data-guide-fullname="' + record.key + '"]'), wholeNames && record.name !== record.fullName);
      });
      if (step.number === 3) guide.shortPending = state === 'current' || state === 'upcoming';
    });
    this.markCurrent(shown);

    // Where the email goes (step 3): a warning opens the forwarding instead of the records.
    var mail = section.querySelector('[data-guide-mail]');
    if (mail) {
      mail.className = 'guide-mail guide-mail--' + (data.mailAtRisk || data.mailBroken ? 'risk' : 'ok');
      text(mail.querySelector('[data-guide-mail-text]'), data.mailNote);
      show(mail, !!data.mailNote);
      var forwarding = section.querySelector('[data-guide-forwarding]');
      var forwardingToggle = section.querySelector('[data-guide-forwarding-toggle]');
      if (data.mailAtRisk && forwarding && !forwarding.classList.contains('show')) {
        forwarding.classList.add('show');
        if (forwardingToggle) forwardingToggle.setAttribute('aria-expanded', 'true');
      }
    }
    // The domain's row says it too when the email has already stopped: the guide may be folded.
    var mailRow = document.querySelector('[data-guide-mail-row="' + this.id + '"]');
    if (mailRow) {
      text(mailRow, data.mailBroken ? data.mailNote : '');
      show(mailRow, !!data.mailBroken);
    }

    var finish = data.finish || [];
    each(section, '[data-guide-progress]', function (line) {
      var key = line.getAttribute('data-guide-progress');
      var found = null;
      finish.forEach(function (item) { if (item.key === key) found = item; });
      show(line, !!found);
      if (!found) return;
      var state = stateClass(found.state);
      line.className = 'guide-progress__line guide-progress--' + state;
      var icon = line.querySelector('[data-guide-progress-icon]');
      if (icon) icon.className = 'fas ' + (state === 'done' ? 'fa-check' : state === 'current' ? 'fa-hourglass-half' : 'fa-circle fa-2xs');
      text(line.querySelector('[data-guide-progress-state]'), state === 'done' ? 'Done:' : state === 'current' ? 'In progress:' : 'Not yet:');
      text(line.querySelector('[data-guide-progress-label]'), found.label);
      text(line.querySelector('[data-guide-progress-detail]'), found.detail);
    });
    var caa = section.querySelector('[data-guide-caa]');
    if (caa) {
      show(caa, !!data.caaName);
      text(caa.querySelector('[data-guide-caa-name]'), data.caaName);
      // Empty is an answer: the hosts that leave the name empty for the domain itself (DnsHosts, RootName).
      text(caa.querySelector('[data-guide-caa-box]'), data.caaNameInBox == null ? '@' : data.caaNameInBox);
      show(caa.querySelector('[data-guide-caa-empty]'), data.caaNameInBox === '');
    }

    text(section.querySelector('[data-guide-checked]'), data.checkedAtUtc ? 'Checked just now.' : 'Checked.');

    // A new current step: open it, say so, and take the focus there if the adviser is working inside the guide.
    var inside = section.contains(document.activeElement);
    var announced = false;
    this.paintMeter(data.live ? 0 : shown);
    this.setTitle(shown, !!data.live);
    if (data.live && !this.live) {
      this.announce(data.heading + '.');
      announced = true;
      this.open(4, inside);
      // Live means proven, pointed and secured (DomainSetupGuide: live needs the certificate).
      this.moved(data.heading + ', secured with SSL. There is nothing more to do.', false);
      this.arrive(4, inside);
    } else if (current !== this.current && current > 0) {
      // What is said is what the steps say: only steps now done count as done (a step jumped over is not).
      var title = '';
      var previous = this.current;
      var doneNow = [];
      (data.steps || []).forEach(function (step) {
        if (step.number === shown) title = step.title;
        if (previous > 0 && step.number >= previous && step.number < shown && stateClass(step.state) === 'done') doneNow.push(step.number);
      });
      var done = doneNow.length === 0 ? ''
        : doneNow.length === 1 ? 'Step ' + doneNow[0] + ' done. '
        : 'Steps ' + doneNow.slice(0, -1).join(', ') + ' and ' + doneNow[doneNow.length - 1] + ' done. ';
      this.announce(done + 'Step ' + shown + ': ' + title + '.');
      announced = true;
      this.open(shown, inside);
      // 603: the same words on the page, until the next change. Going back (a record no longer seen) is amber.
      var back = previous > 0 && shown < previous;
      this.moved(back ? 'Back to step ' + shown + ': ' + title + '. Something this step needs is no longer seen.'
        : done + 'Now step ' + shown + ': ' + title + '.', back);
      this.arrive(shown, inside);
    }
    this.current = current;
    this.live = !!data.live;
    return announced;
  };

  Guide.prototype.start = function (focus) {
    if (this.refused) return;
    if (!this.busy && this.timer === null) this.poll(false);
    // A skipped step 3 stays skipped across reloads in this tab.
    if (this.current === 3 && this.skipped()) {
      this.markCurrent(4);
      this.open(4, false);
    }
    if (focus) {
      var open = this.section.querySelector('.guide-step__toggle[aria-expanded="true"]');
      var title = this.section.querySelector('#' + this.section.id + '-title');
      var target = open || title;
      if (target) {
        if (target === title) title.setAttribute('tabindex', '-1');
        target.focus();
        if (target.scrollIntoView) target.scrollIntoView({ block: 'center' });
      }
    }
  };

  function setUp() {
    var guides = {};
    // Only the guides on screen look again (several open guides, one rate limit per adviser).
    var observer = 'IntersectionObserver' in window ? new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        var guide = guides[entry.target.getAttribute('data-domain-guide')];
        if (!guide) return;
        var was = guide.inView;
        guide.inView = entry.isIntersecting;
        if (!was && guide.inView && !guide.section.hidden && guide.watching() && !guide.busy) guide.poll(false);
      });
    }) : null;

    // 603: the tab's title follows one guide: the domain just added, or else the first open one still being set up.
    var titled = document.querySelector('[data-domain-guide][data-guide-focus="true"]:not([hidden])')
      || document.querySelector('[data-domain-guide][data-guide-live="false"][data-guide-refused="false"]:not([hidden])');
    each(document, '[data-domain-guide]', function (section) {
      var guide = new Guide(section);
      guides[guide.id] = guide;
      if (section === titled) {
        guide.titled = true;
        guide.setTitle(guide.current === 3 && guide.skipped() ? 4 : guide.current, guide.live);
      }
      if (observer) observer.observe(section);
      if (!section.hidden) guide.start(section.getAttribute('data-guide-focus') === 'true');
      // A live domain whose short address points here: one look, open or folded, in case its email depends on the short
      // address (the row says so; 595's third review, L-2).
      if (section.getAttribute('data-guide-mail-once') === 'true' && !guide.busy) guide.poll(false, true);
    });

    // The row's Guide button shows or hides the domain's guide; shown, it looks at once.
    each(document, '[data-guide-toggle]', function (button) {
      button.addEventListener('click', function () {
        var guide = guides[button.getAttribute('data-guide-toggle')];
        if (!guide) return;
        var showIt = guide.section.hidden;
        guide.section.hidden = !showIt;
        button.setAttribute('aria-expanded', showIt ? 'true' : 'false');
        window.clearTimeout(guide.timer);
        guide.timer = null;
        guide.nextAt = 0;
        guide.setTitle(guide.current, guide.live);
        if (showIt) {
          guide.stopped = false;
          guide.start(true);
        }
      });
    });

    // Back in front: look at once rather than wait out the interval.
    document.addEventListener('visibilitychange', function () {
      if (document.visibilityState !== 'visible') return;
      Object.keys(guides).forEach(function (id) {
        var guide = guides[id];
        if (!guide.section.hidden && guide.watching() && !guide.busy) guide.poll(false);
      });
    });
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', setUp); else setUp();
})();
