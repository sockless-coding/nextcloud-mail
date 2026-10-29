<?php

declare(strict_types=1);

// SPDX-License-Identifier: MIT

namespace OCA\SocklessMail\Listener;

use OCA\SocklessMail\AppInfo\Application;
use OCA\SocklessMail\Service\ExApp;
use OCP\EventDispatcher\Event;
use OCP\EventDispatcher\IEventListener;
use OCP\INavigationManager;
use OCP\IURLGenerator;
use OCP\IUserSession;
use OCP\L10N\IFactory;

/**
 * Points AppAPI's app-menu entry for Sockless Mail at /apps/socklessmail/. The navigation
 * manager keys entries by id, and resolves plain entries after the closures that AppAPI adds
 * from this same event, so an entry with AppAPI's id replaces AppAPI's entry.
 *
 * @template-implements IEventListener<Event>
 */
class NavigationListener implements IEventListener {
	public function __construct(
		private readonly ExApp $exApp,
		private readonly INavigationManager $navigationManager,
		private readonly IURLGenerator $urlGenerator,
		private readonly IUserSession $userSession,
		private readonly IFactory $l10nFactory,
	) {
	}

	public function handle(Event $event): void {
		// AppAPI shows the entry to signed-in users only; without the External App there is nothing to replace.
		if ($this->userSession->getUser() === null || !$this->exApp->isEnabled()) {
			return;
		}

		$this->navigationManager->add([
			'id' => ExApp::NAVIGATION_ID,
			'type' => 'link',
			'app' => 'app_api',
			'href' => $this->urlGenerator->linkToRoute(Application::APP_ID . '.page.index'),
			'icon' => $this->exApp->proxyUrl('img/app.svg'),
			'name' => $this->l10nFactory->get(ExApp::APP_ID)->t('Sockless Mail'),
		]);
	}
}
