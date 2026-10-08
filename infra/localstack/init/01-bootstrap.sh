#!/bin/bash
# Provisions PayX's AWS-side resources into LocalStack. LocalStack runs every
# script in /etc/localstack/init/ready.d once its services are up.
#
# Each resource has exactly one owning service (CLAUDE.md architecture table):
#
#   alias/payx-card-vault   (KMS)       -> Vault: the key that wraps the per-card
#                                          data keys (envelope encryption, Module 3.5)
#   payx-card-vault         (DynamoDB)  -> Vault: token -> encrypted card record
#   payx-risk-decisions     (DynamoDB)  -> Risk: one item per scored payment
#   payx-settlement-files   (S3)        -> written by the PSP simulator, read by
#                                          Reconciliation (the doc's end-of-day
#                                          settlement file, Phase 8)
#
# Idempotent: every create is guarded by an existence check, because state can
# survive a container restart on the data volume and a second plain
# create-table would fail the whole script.
set -euo pipefail

REGION="${AWS_DEFAULT_REGION:-us-east-1}"
KMS_ALIAS="alias/payx-card-vault"
VAULT_TABLE="payx-card-vault"
RISK_TABLE="payx-risk-decisions"
SETTLEMENT_BUCKET="payx-settlement-files"

echo "[payx] provisioning local AWS in ${REGION}"

# --- S3 -----------------------------------------------------------------------
if ! awslocal s3api head-bucket --bucket "${SETTLEMENT_BUCKET}" >/dev/null 2>&1; then
  awslocal s3api create-bucket --bucket "${SETTLEMENT_BUCKET}" --region "${REGION}" >/dev/null
fi
echo "[payx] s3 ready: ${SETTLEMENT_BUCKET}"

# --- DynamoDB -----------------------------------------------------------------
# Only the key is declared up front — DynamoDB is schemaless beyond it. The
# other attributes are defined by the owning service's code, in its module.
create_table() {
  local table="$1" key="$2"
  if ! awslocal dynamodb describe-table --table-name "${table}" >/dev/null 2>&1; then
    awslocal dynamodb create-table \
      --table-name "${table}" \
      --attribute-definitions "AttributeName=${key},AttributeType=S" \
      --key-schema "AttributeName=${key},KeyType=HASH" \
      --billing-mode PAY_PER_REQUEST >/dev/null
  fi
  echo "[payx] dynamodb ready: ${table} (key: ${key})"
}
create_table "${VAULT_TABLE}" token
create_table "${RISK_TABLE}"  payment_id

# --- KMS ----------------------------------------------------------------------
# One customer-managed key, referenced everywhere by its alias rather than its
# generated key id — so code and config never change if the key is recreated
# or rotated to a new key behind the same alias.
# Created last: the compose healthcheck waits for this alias, so "healthy"
# means every resource above exists too.
if ! awslocal kms describe-key --key-id "${KMS_ALIAS}" >/dev/null 2>&1; then
  KEY_ID=$(awslocal kms create-key \
    --description "PayX Vault key-encryption key" \
    --key-usage ENCRYPT_DECRYPT \
    --query KeyMetadata.KeyId --output text)
  awslocal kms create-alias --alias-name "${KMS_ALIAS}" --target-key-id "${KEY_ID}"
fi
echo "[payx] kms ready: ${KMS_ALIAS}"
