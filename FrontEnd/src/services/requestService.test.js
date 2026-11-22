import test from 'node:test';
import assert from 'node:assert/strict';

globalThis.sessionStorage = {
    getItem: (key) => key === 'rmsSessionToken' ? 'test-session-token' : null,
    removeItem: () => {},
};

const { default: apiClient } = await import('./api.js');
const { requestService } = await import('./requestService.js');

const requestInterceptor = apiClient.interceptors.request.handlers.find((handler) => handler?.fulfilled);

test('request client attaches the session authorization header', () => {
    const config = requestInterceptor.fulfilled({ headers: {} });
    assert.equal(config.headers.Authorization, 'Bearer test-session-token');
});

test('creates a JSON request at the JSON transaction route', async () => {
    const originalPost = apiClient.post;
    const calls = [];
    apiClient.post = async (...args) => {
        calls.push(args);
        return { data: { id: 10 } };
    };

    try {
        const data = { transactionTypesID: 2, startDate: '2026-11-21', endDate: '2026-11-21' };
        assert.deepEqual(await requestService.create(data), { id: 10 });
        assert.deepEqual(calls, [['/transactions', data]]);
    } finally {
        apiClient.post = originalPost;
    }
});

test('creates a multipart request at the documents transaction route and preserves fields and files', async () => {
    const originalPost = apiClient.post;
    const calls = [];
    apiClient.post = async (...args) => {
        calls.push(args);
        return { data: { id: 11 } };
    };

    try {
        const first = new Blob(['first'], { type: 'application/pdf' });
        const second = new Blob(['second'], { type: 'image/png' });
        await requestService.create({
            transactionTypesID: 1,
            startDate: '2026-11-21',
            endDate: '2026-11-22',
            substituteEmployeeId: null,
            leaveRationale: 'Medical leave',
            medicalDocuments: [first, second],
        });

        assert.equal(calls.length, 1);
        assert.equal(calls[0][0], '/transactions/with-documents');
        assert.equal(calls[0].length, 2, 'no multipart Content-Type is set manually');
        const entries = [...calls[0][1].entries()];
        assert.equal(calls[0][1] instanceof FormData, true);
        assert.deepEqual(entries.filter(([name]) => name === 'MedicalDocuments').map(([, file]) => file.size), [5, 6]);
        assert.deepEqual(entries.filter(([name]) => name !== 'MedicalDocuments'), [
            ['transactionTypesID', '1'], ['startDate', '2026-11-21'], ['endDate', '2026-11-22'],
            ['leaveRationale', 'Medical leave'],
        ]);
    } finally {
        apiClient.post = originalPost;
    }
});

test('updates JSON and multipart requests at their respective transaction routes', async () => {
    const originalPut = apiClient.put;
    const calls = [];
    apiClient.put = async (...args) => {
        calls.push(args);
        return { data: {} };
    };

    try {
        const json = { transactionTypesID: 2, leaveRationale: 'Updated' };
        await requestService.update(42, json);
        await requestService.update(43, {
            transactionTypesID: 1,
            leaveRationale: 'Updated medical leave',
            medicalDocuments: [new Blob(['proof'], { type: 'application/pdf' })],
        });

        assert.deepEqual(calls[0], ['/transactions/42', json]);
        assert.equal(calls[1][0], '/transactions/43/with-documents');
        assert.equal(calls[1].length, 2, 'no multipart Content-Type is set manually');
        assert.equal(calls[1][1] instanceof FormData, true);
        assert.equal(calls[1][1].get('MedicalDocuments').size, 5);
    } finally {
        apiClient.put = originalPut;
    }
});

test('preserves API errors from transaction requests', async () => {
    const originalPost = apiClient.post;
    const expected = new Error('Request rejected by API');
    apiClient.post = async () => { throw expected; };

    try {
        await assert.rejects(
            requestService.create({ transactionTypesID: 2 }),
            (error) => error === expected
        );
    } finally {
        apiClient.post = originalPost;
    }
});
