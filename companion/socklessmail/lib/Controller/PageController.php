<?php

declare(strict_types=1);

// SPDX-License-Identifier: MIT

namespace OCA\SocklessMail\Controller;

use OCA\SocklessMail\AppInfo\Application;
use OCA\SocklessMail\Service\ExApp;
use OCP\AppFramework\Controller;
use OCP\AppFramework\Http\Attribute\FrontpageRoute;
use OCP\AppFramework\Http\Attribute\NoAdminRequired;
use OCP\AppFramework\Http\Attribute\NoCSRFRequired;
use OCP\AppFramework\Http\ContentSecurityPolicy;
use OCP\AppFramework\Http\NotFoundResponse;
use OCP\AppFramework\Http\RedirectResponse;
use OCP\AppFramework\Http\Response;
use OCP\AppFramework\Http\TemplateResponse;
use OCP\AppFramework\Services\IInitialState;
use OCP\INavigationManager;
use OCP\IRequest;
use OCP\Util;

/**
 * The mail page: the same empty #content element as AppAPI's page, with the External App's
 * initial state and bundles. AppAPI's page adds the bundles through a middleware that only runs
 * for its own controller, so a small loader (js/socklessmail-main.js) adds them here.
 */
class PageController extends Controller {
	public function __construct(
		IRequest $request,
		private readonly ExApp $exApp,
		private readonly IInitialState $initialState,
		private readonly INavigationManager $navigationManager,
	) {
		parent::__construct(Application::APP_ID, $request);
	}

	/** Paths below the page are the app's own (none yet); they all show the mail page. */
	#[NoAdminRequired]
	#[NoCSRFRequired]
	#[FrontpageRoute(verb: 'GET', url: '/')]
	#[FrontpageRoute(verb: 'GET', url: '/{path}', requirements: ['path' => '.+'], postfix: 'path')]
	public function index(string $path = ''): Response {
		if (!$this->exApp->isEnabled()) {
			return new NotFoundResponse();
		}
		$states = $this->exApp->initialStates();
		if ($states === null) {
			// AppAPI changed in a way this app does not know: its own page still works.
			return new RedirectResponse($this->exApp->embeddedUrl());
		}

		foreach ($states as $key => $value) {
			$this->initialState->provideInitialState($key, $value);
		}
		$this->initialState->provideInitialState('bundles', [
			'script' => $this->exApp->proxyUrl(ExApp::SCRIPT_PATH),
			'style' => $this->exApp->proxyUrl(ExApp::STYLE_PATH),
		]);
		Util::addScript(Application::APP_ID, 'socklessmail-main');
		$this->navigationManager->setActiveEntry(ExApp::NAVIGATION_ID);

		$response = new TemplateResponse(Application::APP_ID, 'main');
		// What AppAPI's page allows (TopMenuController in AppAPI).
		$csp = new ContentSecurityPolicy();
		$csp->addAllowedScriptDomain($this->request->getServerHost());
		$csp->addAllowedScriptDomain('\'unsafe-eval\'');
		$csp->addAllowedScriptDomain('\'unsafe-inline\'');
		$csp->addAllowedFrameDomain($this->request->getServerHost());
		$response->setContentSecurityPolicy($csp);
		return $response;
	}
}
