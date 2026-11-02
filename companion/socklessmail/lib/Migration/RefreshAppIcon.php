<?php

declare(strict_types=1);

// SPDX-License-Identifier: MIT

namespace OCA\SocklessMail\Migration;

use OCA\Theming\ThemingDefaults;
use OCP\App\IAppManager;
use OCP\Migration\IOutput;
use OCP\Migration\IRepairStep;
use Psr\Container\ContainerInterface;

/**
 * The theming app renders this app's icons (favicon, the installed web app's icon) once and keeps
 * them until its cache-buster changes. Icons rendered before img/app.svg existed, or from an older
 * one, would stay; a new cache-buster has them rendered again. It costs only a re-render of the
 * cached theming images, the same as an admin changing the theme.
 */
class RefreshAppIcon implements IRepairStep {
	public function __construct(
		private IAppManager $appManager,
		private ContainerInterface $container,
	) {
	}

	public function getName(): string {
		return 'Refresh the Sockless Mail app icon';
	}

	public function run(IOutput $output): void {
		if (!$this->appManager->isEnabledForAnyone('theming') || !class_exists(ThemingDefaults::class)) {
			return;
		}
		$this->container->get(ThemingDefaults::class)->increaseCacheBuster();
	}
}
