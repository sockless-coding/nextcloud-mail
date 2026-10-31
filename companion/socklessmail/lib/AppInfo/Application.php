<?php

declare(strict_types=1);

// SPDX-License-Identifier: MIT

namespace OCA\SocklessMail\AppInfo;

use OCA\SocklessMail\Listener\NavigationListener;
use OCP\AppFramework\App;
use OCP\AppFramework\Bootstrap\IBootContext;
use OCP\AppFramework\Bootstrap\IBootstrap;
use OCP\AppFramework\Bootstrap\IRegistrationContext;
use OCP\Navigation\Events\LoadAdditionalEntriesEvent;

class Application extends App implements IBootstrap {
	public const APP_ID = 'socklessmail';

	public function __construct() {
		parent::__construct(self::APP_ID);
	}

	/** Ahead of AppAPI's listener (priority 0), so this entry replaces AppAPI's (NavigationListener). */
	private const NAVIGATION_PRIORITY = 100;

	public function register(IRegistrationContext $context): void {
		$context->registerEventListener(LoadAdditionalEntriesEvent::class, NavigationListener::class, self::NAVIGATION_PRIORITY);
	}

	public function boot(IBootContext $context): void {
	}
}
