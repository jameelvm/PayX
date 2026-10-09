#!/bin/bash
# Creates PayX's Kafka topics. Run once by the one-shot `kafka-init` container
# after the broker is healthy; --if-not-exists makes re-runs harmless.
#
# Doc ch.2, "Transaction completion": "When a payment is initiated, we publish
# an event to a Kafka topic. Consumer services process these events…"
#
# ONE topic per aggregate, not one per event type.
#   payx.payments   carries payment.authorized, payment.captured,
#                   payment.refunded… with the type inside the message.
# Kafka only guarantees order *within a partition*. With the message key set
# to payment_id, every event for one payment lands in the same partition of
# this one topic, so the Ledger always sees "authorized" before "captured".
# Split them into payx.payments.authorized and payx.payments.captured and that
# guarantee is gone: two topics are two independent logs, and a consumer can
# read the capture first.
#
# Partitions = the unit of parallelism. 6 means at most 6 consumers in one
# group can share the work; a 7th would sit idle. Partitions can be added
# later but never removed, and adding them changes which partition a key maps
# to — so pick generously up front. At the doc's ~1,400 TPS peak, 6 is ample.
#
# Replication factor 1 because there is one local broker. Production (MSK):
# replication-factor 3, min.insync.replicas 2, producers with acks=all — a
# write is confirmed only once two brokers hold it, so losing a broker loses
# no payment event.
set -euo pipefail

BOOTSTRAP="kafka:9092"
TOPICS=/opt/kafka/bin/kafka-topics.sh

$TOPICS --bootstrap-server "$BOOTSTRAP" --create --if-not-exists \
  --topic payx.payments \
  --partitions 6 \
  --replication-factor 1 \
  --config retention.ms=604800000   # 7 days: long enough to replay into a rebuilt Ledger

$TOPICS --bootstrap-server "$BOOTSTRAP" --describe --topic payx.payments
echo "[payx] kafka topics ready"
