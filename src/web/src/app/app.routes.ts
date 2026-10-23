import { inject } from '@angular/core';
import { CanActivateFn, Router, Routes } from '@angular/router';
import { map } from 'rxjs';
import { Auth } from './core/auth';
import { authGuard } from './core/http';
import { Actions } from './core/models';
import { BrokerPage, brokerPages } from './pages/broker-page';

/** "/" → first cluster the user can access. */
const firstCluster: CanActivateFn = () => {
  const auth = inject(Auth);
  const router = inject(Router);
  return auth.ensureLoaded().pipe(
    map((ok) => {
      if (!ok) return router.createUrlTree(['/login']);
      const cluster = auth.me()?.clusters[0];
      return cluster ? router.createUrlTree(['/c', cluster.name]) : router.createUrlTree(['/no-access']);
    }),
  );
};

/** "/c/:cluster" → first page the user is allowed to see in that cluster. */
const clusterHome: CanActivateFn = (route) => {
  const auth = inject(Auth);
  const router = inject(Router);
  const cluster = route.parent?.paramMap.get('cluster') ?? '';
  // IBM MQ and MSMQ have neither exchanges nor shovels.
  const broker = auth.me()?.clusters.find((c) => c.name === cluster)?.broker;
  const rabbit = broker !== 'ibmmq' && broker !== 'msmq';
  const page = auth.can(cluster, Actions.overviewRead) || auth.can(cluster, Actions.nodesRead)
    ? 'overview'
    : auth.can(cluster, Actions.queuesRead) || !rabbit
      ? 'queues'
      : auth.can(cluster, Actions.exchangesRead)
        ? 'exchanges'
        : 'shovels';
  return router.createUrlTree(['/c', cluster, page]);
};

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./pages/login/login').then((m) => m.Login) },
  {
    path: 'no-access',
    canActivate: [authGuard],
    loadComponent: () => import('./pages/login/no-access').then((m) => m.NoAccess),
  },
  { path: '', pathMatch: 'full', canActivate: [firstCluster], children: [] },
  {
    path: 'c/:cluster',
    canActivate: [authGuard],
    loadComponent: () => import('./pages/shell/shell').then((m) => m.Shell),
    children: [
      { path: '', pathMatch: 'full', canActivate: [clusterHome], children: [] },
      {
        path: 'overview',
        component: BrokerPage,
        data: brokerPages({
          rabbitmq: () => import('./pages/overview/overview').then((m) => m.OverviewPage),
          ibmmq: () => import('./pages/mq/mq-overview').then((m) => m.MqOverviewPage),
          msmq: () => import('./pages/msmq/msmq-overview').then((m) => m.MsmqOverviewPage),
        }),
      },
      {
        path: 'queues',
        component: BrokerPage,
        data: brokerPages({
          rabbitmq: () => import('./pages/queues/queues').then((m) => m.QueuesPage),
          ibmmq: () => import('./pages/mq/mq-queues').then((m) => m.MqQueuesPage),
          msmq: () => import('./pages/msmq/msmq-queues').then((m) => m.MsmqQueuesPage),
        }),
      },
      {
        path: 'queues/:vhost/:name',
        component: BrokerPage,
        data: brokerPages({
          rabbitmq: () => import('./pages/queue/queue').then((m) => m.QueuePage),
          ibmmq: () => import('./pages/mq/mq-queue').then((m) => m.MqQueuePage),
          msmq: () => import('./pages/msmq/msmq-queue').then((m) => m.MsmqQueuePage),
        }),
      },
      {
        path: 'exchanges',
        component: BrokerPage,
        data: brokerPages({ rabbitmq: () => import('./pages/exchanges/exchanges').then((m) => m.ExchangesPage) }),
      },
      {
        path: 'shovels',
        component: BrokerPage,
        data: brokerPages({ rabbitmq: () => import('./pages/shovels/shovels').then((m) => m.ShovelsPage) }),
      },
      { path: 'staging', loadComponent: () => import('./pages/staging/staging').then((m) => m.StagingPage) },
      { path: 'audit', loadComponent: () => import('./pages/audit/audit').then((m) => m.AuditPage) },
    ],
  },
  { path: '**', redirectTo: '' },
];
