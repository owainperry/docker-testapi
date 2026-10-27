# Local equivalents of the CI steps, plus run targets.
# Needs the .NET 10 SDK; the docker-* targets need Docker with buildx; the fga-* targets need
# Docker, the OpenFGA CLI (`fga`) and jq.

IMAGE    ?= operry/docker-testapi
TAG      ?= dev
# Baked into the assembly; must be valid semver, unlike TAG.
VERSION  ?= 0.0.0-$(TAG)
PORT     ?= 8080
# The API refuses to start without it: make run AUDIENCE=<API_APP_ID>
AUDIENCE ?= $(Frontegg__Audience)

# Local OpenFGA (in memory: everything is lost when the container stops).
FGA_IMAGE ?= openfga/openfga:v1.22.0
FGA_PORT  ?= 8090
FGA_URL   ?= http://localhost:$(FGA_PORT)
# Written by `make fga-init`: OpenFga__StoreId and OpenFga__AuthorizationModelId. Not secrets.
-include .openfga.env
export OpenFga__StoreId OpenFga__AuthorizationModelId

.DEFAULT_GOAL := help
.PHONY: help build test test-integration run token docker-build docker-run clean check-audience \
        fga-up fga-down fga-init fga-model fga-test

help: ## List targets
	@grep -E '^[a-z-]+:.*## ' $(MAKEFILE_LIST) | awk -F':.*## ' '{printf "  %-14s %s\n", $$1, $$2}'

build: ## Build the solution (Release), as CI does
	dotnet build DemoApi.slnx -c Release

test: build ## Run the tests, as CI does
	dotnet test DemoApi.slnx -c Release --no-build

test-integration: build ## Also run the tests against the local OpenFGA (after fga-up fga-init)
	@test -n "$(OpenFga__StoreId)" || { echo "No store yet: make fga-up fga-init" >&2; exit 1; }
	OPENFGA_TEST_API_URL=$(FGA_URL) OPENFGA_TEST_STORE_ID=$(OpenFga__StoreId) dotnet test DemoApi.slnx -c Release --no-build

run: check-audience ## Run the API on localhost:5080 (needs AUDIENCE)
	Frontegg__Audience=$(AUDIENCE) dotnet run --project DemoApi

token: ## Get a real token: make token APP_ID=<frontegg app id> (browser sign-in, no secret)
	@test -n "$(APP_ID)" || { echo "APP_ID is not set: make token APP_ID=<frontegg app id>" >&2; exit 1; }
	@dotnet run --project TokenHelper -- --app-id $(APP_ID)

docker-build: ## Build the image for this machine (IMAGE, TAG)
	docker buildx build --load --build-arg VERSION=$(VERSION) -t $(IMAGE):$(TAG) .

docker-run: check-audience ## Run the image on localhost:PORT, default 8080 (needs AUDIENCE)
	docker run --rm -p $(PORT):8080 -e Frontegg__Audience=$(AUDIENCE) \
	  --add-host=host.docker.internal:host-gateway \
	  -e OpenFga__ApiUrl=http://host.docker.internal:$(FGA_PORT) \
	  -e OpenFga__StoreId=$(OpenFga__StoreId) -e OpenFga__AuthorizationModelId=$(OpenFga__AuthorizationModelId) \
	  $(IMAGE):$(TAG)

fga-up: ## Start OpenFGA in Docker: API on localhost:FGA_PORT (8090), playground on localhost:3000
	docker run -d --name qauth-openfga -p $(FGA_PORT):8080 -p 3000:3000 $(FGA_IMAGE) run
	@for i in $$(seq 30); do curl -sf $(FGA_URL)/healthz >/dev/null && exit 0; sleep 1; done; echo "OpenFGA didn't start" >&2; exit 1

fga-down: ## Stop OpenFGA (its in-memory store goes with it)
	docker rm -f qauth-openfga
	rm -f .openfga.env

fga-init: ## Create the store from openfga/store.fga.yaml (model + seed tuples); IDs go to .openfga.env
	fga store import --api-url $(FGA_URL) --file openfga/store.fga.yaml 2>/dev/null \
	  | jq -r '"OpenFga__StoreId=\(.store.id)\nOpenFga__AuthorizationModelId=\(.model.authorization_model_id)"' > .openfga.env
	@cat .openfga.env

fga-model: ## Upload openfga/model.fga as a new model version and pin the API to it (restart the API)
	@test -n "$(OpenFga__StoreId)" || { echo "No store yet: make fga-up fga-init" >&2; exit 1; }
	@id=$$(fga model write --api-url $(FGA_URL) --store-id $(OpenFga__StoreId) --file openfga/model.fga | jq -r .authorization_model_id) && \
	  printf 'OpenFga__StoreId=%s\nOpenFga__AuthorizationModelId=%s\n' "$(OpenFga__StoreId)" "$$id" > .openfga.env && cat .openfga.env

fga-test: ## Run the model's own tests (openfga/store.fga.yaml), no server needed
	fga model test --tests openfga/store.fga.yaml

clean: ## Remove build output
	dotnet clean DemoApi.slnx -c Release
	rm -rf */bin */obj TestResults

check-audience:
	@test -n "$(AUDIENCE)" || { echo "AUDIENCE is not set: make $(MAKECMDGOALS) AUDIENCE=<API_APP_ID>" >&2; exit 1; }
