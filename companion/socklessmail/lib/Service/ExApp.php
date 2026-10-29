<?php

declare(strict_types=1);

// SPDX-License-Identifier: MIT

namespace OCA\SocklessMail\Service;

use OCA\AppAPI\Service\ExAppService;
use OCA\AppAPI\Service\UI\InitialStateService;
use OCP\App\IAppManager;
use OCP\IURLGenerator;
use Psr\Container\ContainerInterface;
use Psr\Log\LoggerInterface;
use Throwable;

/**
 * What this app needs from AppAPI about the Sockless Mail External App. AppAPI has no public
 * API for it, so its services are used directly; every call is guarded, and callers fall back
 * to AppAPI's own page when something is missing or has changed.
 */
class ExApp {
	public const APP_ID = 'sockless_mail';

	/** The top-menu entry the External App registers (UiRegistration.cs). */
	public const MENU_NAME = 'sockless_mail';

	/** The id AppAPI gives that entry in the app menu (LoadMenuEntriesListener in AppAPI). */
	public const NAVIGATION_ID = 'app_api_' . self::APP_ID . '_' . self::MENU_NAME;

	/** The bundles the External App serves (src/frontend/vite.config.js). */
	public const SCRIPT_PATH = 'js/sockless_mail-main.js';
	public const STYLE_PATH = 'css/sockless_mail-main.css';

	public function __construct(
		private readonly IAppManager $appManager,
		private readonly IURLGenerator $urlGenerator,
		private readonly ContainerInterface $container,
		private readonly LoggerInterface $logger,
	) {
	}

	/** Whether AppAPI is enabled and knows an enabled Sockless Mail External App. */
	public function isEnabled(): bool {
		if (!$this->appManager->isEnabledForAnyone('app_api') || !class_exists(ExAppService::class)) {
			return false;
		}
		try {
			return (bool)$this->container->get(ExAppService::class)->getExApp(self::APP_ID)?->getEnabled();
		} catch (Throwable $e) {
			$this->logger->warning('Could not ask AppAPI about the Sockless Mail External App', ['exception' => $e]);
			return false;
		}
	}

	/**
	 * The initial state the External App registered for its page, as key => value; null when
	 * AppAPI cannot provide it.
	 *
	 * @return array<string, mixed>|null
	 */
	public function initialStates(): ?array {
		if (!class_exists(InitialStateService::class)) {
			return null;
		}
		try {
			return $this->container->get(InitialStateService::class)
				->getExAppInitialStates(self::APP_ID, 'top_menu', self::MENU_NAME);
		} catch (Throwable $e) {
			$this->logger->warning('Could not read the Sockless Mail initial state from AppAPI', ['exception' => $e]);
			return null;
		}
	}

	/** A file the External App serves, through AppAPI's proxy (works with every deploy daemon). */
	public function proxyUrl(string $path): string {
		return $this->urlGenerator->linkToRoute('app_api.ExAppProxy.ExAppGet', ['appId' => self::APP_ID, 'other' => $path]);
	}

	/** AppAPI's own page for the External App. */
	public function embeddedUrl(): string {
		return $this->urlGenerator->linkToRoute('app_api.TopMenu.viewExAppPage', ['appId' => self::APP_ID, 'name' => self::MENU_NAME]);
	}
}
