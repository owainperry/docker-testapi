# Local equivalents of the CI steps, plus run targets.
# Needs the .NET 10 SDK; the docker-* targets need Docker with buildx.

IMAGE    ?= operry/docker-testapi
TAG      ?= dev
PORT     ?= 8080
# The API refuses to start without it: make run AUDIENCE=<API_APP_ID>
AUDIENCE ?= $(Frontegg__Audience)

.DEFAULT_GOAL := help
.PHONY: help build test run token docker-build docker-run clean check-audience

help: ## List targets
	@grep -E '^[a-z-]+:.*## ' $(MAKEFILE_LIST) | awk -F':.*## ' '{printf "  %-14s %s\n", $$1, $$2}'

build: ## Build the solution (Release), as CI does
	dotnet build DemoApi.slnx -c Release

test: build ## Run the tests, as CI does
	dotnet test DemoApi.slnx -c Release --no-build

run: check-audience ## Run the API on localhost:5080 (needs AUDIENCE)
	Frontegg__Audience=$(AUDIENCE) dotnet run --project DemoApi

token: ## Get a real token: make token APP_ID=<frontegg app id> (secret from FE_APP_SECRET or prompted)
	@test -n "$(APP_ID)" || { echo "APP_ID is not set: make token APP_ID=<frontegg app id>" >&2; exit 1; }
	@dotnet run --project TokenHelper -- --app-id $(APP_ID)

docker-build: ## Build the image for this machine (IMAGE, TAG)
	docker buildx build --load --build-arg VERSION=$(TAG) -t $(IMAGE):$(TAG) .

docker-run: check-audience ## Run the image on localhost:PORT, default 8080 (needs AUDIENCE)
	docker run --rm -p $(PORT):8080 -e Frontegg__Audience=$(AUDIENCE) $(IMAGE):$(TAG)

clean: ## Remove build output
	dotnet clean DemoApi.slnx -c Release
	rm -rf */bin */obj TestResults

check-audience:
	@test -n "$(AUDIENCE)" || { echo "AUDIENCE is not set: make $(MAKECMDGOALS) AUDIENCE=<API_APP_ID>" >&2; exit 1; }
